using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Barrios;

/// <summary>
/// Test de integración end-to-end del <c>BarrioController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant (auth por cookie → claim negocioId → query filter);</item>
///   <item>unicidad de nombre por negocio (409) y reutilización entre negocios;</item>
///   <item>los listados/detalle PÚBLICOS por <c>?negocio=slug</c> (mecanismo <c>[AllowAnonymousWithTenant]</c>):
///   slug válido, faltante (400) e inexistente (404), incluido el filtro <c>?activo=</c>;</item>
///   <item>el cierre del leak cross-tenant de <c>GET /barrios/{id}</c>: con slug del negocio dueño se ve;
///   con slug de otro negocio da 404.</item>
/// </list>
/// </summary>
[Collection(BarrioApiCollection.Name)]
public sealed class BarriosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-ba-a";
    private const string NegocioBId = "neg-ba-b";
    private const string NegocioASlug = "negocio-ba-a";
    private const string NegocioBSlug = "negocio-ba-b";
    private const string AdminAId = "user-ba-a";
    private const string AdminBId = "user-ba-b";
    private const string AdminAEmail = "admin-a@ba.test";
    private const string AdminBEmail = "admin-b@ba.test";

    private readonly BarrioWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio BA A", NegocioASlug, AdminAId, AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio BA B", NegocioBSlug, AdminBId, AdminBEmail, now);
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
    public async Task Aislamiento_unicidad_y_lectura_publica()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var centroA = await CrearAsync(clientA, new { nombre = "Centro", precioEnvio = 500.0 });
        Assert.True(centroA.Activo); // default true
        Assert.Equal(500, centroA.PrecioEnvio);

        var norteA = await CrearAsync(clientA, new { nombre = "Norte", precioEnvio = 800.0, activo = false });
        Assert.False(norteA.Activo);

        var centroB = await CrearAsync(clientB, new { nombre = "Centro", precioEnvio = 999.0 }); // mismo nombre, otro negocio

        // Unicidad por negocio: A no puede repetir "Centro" → 409.
        var dup = await clientA.PostAsJsonAsync("/barrios", new { nombre = "Centro", precioEnvio = 100.0 });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // ── Lectura PÚBLICA de A por slug (anónimo) ───────────────────────────────────────────
        var anon = _factory.CreateClient();
        var publicosA = await GetArrayAsync(anon, $"/barrios?negocio={NegocioASlug}");
        Assert.Equal(2, publicosA.Length);
        Assert.DoesNotContain(publicosA, b => b.Id == centroB.Id);

        // Filtro ?activo=true → sólo Centro.
        var soloActivos = await GetArrayAsync(anon, $"/barrios?negocio={NegocioASlug}&activo=true");
        Assert.Single(soloActivos);
        Assert.Equal(centroA.Id, soloActivos[0].Id);

        // Sin slug → 400; slug inexistente → 404.
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/barrios")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/barrios?negocio=no-existe")).StatusCode);
    }

    [Fact]
    public async Task GetById_publico_respeta_el_tenant_por_slug()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var barrioA = await CrearAsync(clientA, new { nombre = "Sur", precioEnvio = 700.0 });

        var anon = _factory.CreateClient();

        // Con el slug del negocio dueño → 200.
        var propio = await anon.GetAsync($"/barrios/{barrioA.Id}?negocio={NegocioASlug}");
        Assert.Equal(HttpStatusCode.OK, propio.StatusCode);

        // Con el slug de OTRO negocio → 404 (cerrado el leak cross-tenant de NestJS).
        var ajeno = await anon.GetAsync($"/barrios/{barrioA.Id}?negocio={NegocioBSlug}");
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);

        // Sin slug ni sesión → 400 (no se puede resolver el tenant).
        var sinSlug = await anon.GetAsync($"/barrios/{barrioA.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, sinSlug.StatusCode);
    }

    [Fact]
    public async Task Update_y_delete_admin()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var barrio = await CrearAsync(clientA, new { nombre = "Oeste", precioEnvio = 300.0 });

        // Update parcial.
        var actualizado = await PatchAsync(clientA, $"/barrios/{barrio.Id}", new { precioEnvio = 1200.0, activo = false });
        Assert.Equal(1200, actualizado.PrecioEnvio);
        Assert.False(actualizado.Activo);

        // Delete → 204, y luego 404 (con sesión, el tenant viene del claim).
        var borrado = await clientA.DeleteAsync($"/barrios/{barrio.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrado.StatusCode);
        var yaNoEsta = await clientA.GetAsync($"/barrios/{barrio.Id}");
        Assert.Equal(HttpStatusCode.NotFound, yaNoEsta.StatusCode);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<BarrioDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/barrios", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BarrioDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<BarrioDto> PatchAsync(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BarrioDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<BarrioDto[]> GetArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<BarrioDto[]>();
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

    private sealed record BarrioDto(string Id, string Nombre, double PrecioEnvio, bool Activo);
}
