using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Categorias;

/// <summary>
/// Test de integración end-to-end del <c>CategoriasController</c> real (in-memory) contra una
/// base PostgreSQL dedicada. Verifica el aislamiento multi-tenant a través de toda la pila
/// (auth por cookie → claim negocioId → query filter): el negocio A no ve ni accede a las
/// categorías del negocio B, y la unicidad de nombre es por negocio.
/// </summary>
[Collection(CategoriasApiCollection.Name)]
public sealed class CategoriasIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioASlug = "negocio-cat-a";
    private const string NegocioBSlug = "negocio-cat-b";
    private const string AdminAEmail = "admin-a@cat.test";
    private const string AdminBEmail = "admin-b@cat.test";

    private readonly CategoriasWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        SeedNegocioConAdmin(db, hasher, "neg-cat-a", "Negocio Cat A", NegocioASlug, "user-cat-a", AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, "neg-cat-b", "Negocio Cat B", NegocioBSlug, "user-cat-b", AdminBEmail, now);

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await db.Database.EnsureDeletedAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Aislamiento_multitenant_de_categorias_end_to_end()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        // ── Negocio A crea dos categorías ────────────────────────────────────
        var pizzasA = await CrearCategoriaAsync(clientA, "Pizzas", orden: 0);
        var bebidasA = await CrearCategoriaAsync(clientA, "Bebidas", orden: 1);

        // Default de gratuidad: viaja como maxAderezosGratis (no maxSalsasGratis) = 2.
        Assert.Equal(2, pizzasA.MaxAderezosGratis);

        // ── Negocio B crea una categoría con un nombre que A también usa ──────
        // La unicidad es por negocio, así que "Pizzas" en B debe poder crearse.
        var pizzasB = await CrearCategoriaAsync(clientB, "Pizzas", orden: 0);

        // ── A sólo ve SUS categorías ─────────────────────────────────────────
        var categoriasA = await ListarCategoriasAsync(clientA);
        Assert.Equal(2, categoriasA.Length);
        Assert.Equal(new[] { "Pizzas", "Bebidas" }, categoriasA.Select(c => c.Nombre).ToArray()); // ordenadas por Orden
        Assert.DoesNotContain(categoriasA, c => c.Id == pizzasB.Id);

        // ── B sólo ve SU categoría ───────────────────────────────────────────
        var categoriasB = await ListarCategoriasAsync(clientB);
        Assert.Single(categoriasB);
        Assert.Equal(pizzasB.Id, categoriasB[0].Id);
        Assert.DoesNotContain(categoriasB, c => c.Id == pizzasA.Id || c.Id == bebidasA.Id);

        // ── A no puede acceder a la categoría de B por id (404, no 403) ───────
        var crossTenantGet = await clientA.GetAsync($"/categorias/{pizzasB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantGet.StatusCode);

        // ── Unicidad de nombre dentro del negocio: A repite "Pizzas" => 409 ───
        var duplicada = await clientA.PostAsJsonAsync("/categorias", new { nombre = "Pizzas" });
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
    }

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CategoriaDto> CrearCategoriaAsync(HttpClient client, string nombre, int orden)
    {
        var response = await client.PostAsJsonAsync("/categorias", new { nombre, orden });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CategoriaDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<CategoriaDto[]> ListarCategoriasAsync(HttpClient client)
    {
        var response = await client.GetAsync("/categorias");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<CategoriaDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static void SeedNegocioConAdmin(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, DateTime now)
    {
        db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = negocioNombre,
            Slug = slug,
            Activo = true,
            Plan = "basic",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            Password = hasher.Hash(Password),
            Nombre = "Admin",
            Role = Role.ADMIN,
            Activo = true,
            NegocioId = negocioId,
            EmailVerificado = true,
            CreatedAt = now,
        });
    }

    private sealed record CategoriaDto(
        string Id,
        string Nombre,
        string? Descripcion,
        bool Activo,
        int Orden,
        int MaxAderezosGratis,
        DateTime CreatedAt,
        DateTime UpdatedAt);
}
