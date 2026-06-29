using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.ToppingGrupos;

/// <summary>
/// Test de integración end-to-end del <c>ToppingGrupoController</c> real (in-memory) contra una
/// base PostgreSQL dedicada. Verifica el aislamiento multi-tenant (auth por cookie → claim
/// negocioId → query filter), que la unicidad de nombre NO se impone (paridad con NestJS) y que
/// borrar un grupo con Extras no se bloquea sino que los deja huérfanos (FK SET NULL).
/// </summary>
[Collection(ToppingGruposApiCollection.Name)]
public sealed class ToppingGruposIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-tg-a";
    private const string NegocioBId = "neg-tg-b";
    private const string NegocioASlug = "negocio-tg-a";
    private const string NegocioBSlug = "negocio-tg-b";
    private const string AdminAEmail = "admin-a@tg.test";
    private const string AdminBEmail = "admin-b@tg.test";

    private readonly ToppingGruposWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio TG A", NegocioASlug, "user-tg-a", AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio TG B", NegocioBSlug, "user-tg-b", AdminBEmail, now);
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
    public async Task Aislamiento_multitenant_de_topping_grupos_end_to_end()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        // ── Negocio A crea dos grupos; el segundo con un maxExtrasGratis no-default (5) ──────
        var salsasA = await CrearGrupoAsync(clientA, new { nombre = "Salsas", orden = 0 });
        var premiumA = await CrearGrupoAsync(clientA, new { nombre = "Premium", orden = 1, maxExtrasGratis = 5 });

        // El default de gratuidad del primero viaja como maxExtrasGratis = 3.
        Assert.Equal(3, salsasA.MaxExtrasGratis);

        // ── Sin unicidad de nombre: A puede repetir "Salsas" (paridad con NestJS) ────────────
        var salsasDuplicado = await clientA.PostAsJsonAsync("/topping-grupos", new { nombre = "Salsas" });
        Assert.Equal(HttpStatusCode.Created, salsasDuplicado.StatusCode);

        // ── Negocio B crea un grupo con un nombre que A también usa ──────────────────────────
        var salsasB = await CrearGrupoAsync(clientB, new { nombre = "Salsas", orden = 0 });

        // ── A sólo ve SUS grupos (los 3 que creó), nunca el de B ─────────────────────────────
        var gruposA = await ListarGruposAsync(clientA);
        Assert.Equal(3, gruposA.Length);
        Assert.All(gruposA, g => Assert.NotEqual(salsasB.Id, g.Id));

        // ── B sólo ve SU grupo ───────────────────────────────────────────────────────────────
        var gruposB = await ListarGruposAsync(clientB);
        Assert.Single(gruposB);
        Assert.Equal(salsasB.Id, gruposB[0].Id);
        Assert.DoesNotContain(gruposB, g => g.Id == salsasA.Id || g.Id == premiumA.Id);

        // ── A no puede acceder al grupo de B por id (404, no 403) ────────────────────────────
        var crossTenantGet = await clientA.GetAsync($"/topping-grupos/{salsasB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantGet.StatusCode);

        // ── Un valor no-default de maxExtrasGratis se persiste tal cual (releído de DB) ──────
        var premiumLeido = await GetGrupoAsync(clientA, premiumA.Id);
        Assert.Equal(5, premiumLeido.MaxExtrasGratis);
    }

    [Fact]
    public async Task Borrar_grupo_con_extras_no_bloquea_y_orfana_los_extras()
    {
        const string grupoId = "tg-con-extra";
        const string extraId = "extra-huerfano";

        // Seed directo por DbContext: un grupo de A con un Extra que lo referencia.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            db.ToppingGrupos.Add(new ToppingGrupo
            {
                Id = grupoId,
                Nombre = "Grupo con extra",
                MaxExtrasGratis = 3,
                EsIncluido = true,
                Orden = 0,
                Activo = true,
                NegocioId = NegocioAId,
            });
            db.Extras.Add(new Extra
            {
                Id = extraId,
                Nombre = "Extra A1",
                Categoria = "general",
                Precio = 100,
                StockActual = 0,
                Activo = true,
                EsGlobal = false,
                EsPremium = false,
                UnidadMedida = UnidadMedida.UNIDAD,
                ToppingGrupoId = grupoId,
                NegocioId = NegocioAId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // DELETE del grupo vía API real → 204 (no se bloquea pese a tener un Extra asociado).
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var deleteResponse = await clientA.DeleteAsync($"/topping-grupos/{grupoId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // El Extra sobrevive con toppingGrupoId = null (FK SET NULL). Se lee con IgnoreQueryFilters
        // porque en un scope manual no hay HttpContext → el tenant no se resuelve.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            var grupoBorrado = await db.ToppingGrupos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(g => g.Id == grupoId);
            Assert.Null(grupoBorrado);

            var extra = await db.Extras.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == extraId);
            Assert.NotNull(extra);
            Assert.Null(extra!.ToppingGrupoId);
        }
    }

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ToppingGrupoDto> CrearGrupoAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/topping-grupos", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ToppingGrupoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ToppingGrupoDto> GetGrupoAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/topping-grupos/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ToppingGrupoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ToppingGrupoDto[]> ListarGruposAsync(HttpClient client)
    {
        var response = await client.GetAsync("/topping-grupos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<ToppingGrupoDto[]>();
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

    private sealed record ToppingGrupoDto(
        string Id,
        string Nombre,
        int MaxExtrasGratis,
        bool EsIncluido,
        int Orden,
        bool Activo);
}
