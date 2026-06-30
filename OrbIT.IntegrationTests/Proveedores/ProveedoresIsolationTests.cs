using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Proveedores;

/// <summary>
/// Test de integración end-to-end del <c>ProveedorController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant (auth por cookie → claim negocioId → query filter);</item>
///   <item>unicidad de nombre por negocio (409) y reutilización entre negocios;</item>
///   <item>el guard de borrado con insumos asignados (400) — que en NestJS es el único guard, porque la
///   FK es ON DELETE SET NULL — y el borrado exitoso (204) tras desasignar;</item>
///   <item>baja/alta lógica.</item>
/// </list>
/// </summary>
[Collection(ProveedorApiCollection.Name)]
public sealed class ProveedoresIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-pr-a";
    private const string NegocioBId = "neg-pr-b";
    private const string NegocioASlug = "negocio-pr-a";
    private const string NegocioBSlug = "negocio-pr-b";
    private const string AdminAId = "user-pr-a";
    private const string AdminBId = "user-pr-b";
    private const string AdminAEmail = "admin-a@pr.test";
    private const string AdminBEmail = "admin-b@pr.test";

    private readonly ProveedorWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio PR A", NegocioASlug, AdminAId, AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio PR B", NegocioBSlug, AdminBId, AdminBEmail, now);
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
    public async Task Aislamiento_multitenant_y_unicidad_de_nombre()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var cocaA = await CrearAsync(clientA, new { nombre = "Coca", telefono = "111", email = "coca@a.test" });
        Assert.True(cocaA.Activo);
        Assert.Equal("111", cocaA.Telefono);

        var pepsiB = await CrearAsync(clientB, new { nombre = "Pepsi" });

        // A sólo ve los suyos.
        var proveedoresA = await ListarAsync(clientA);
        Assert.Single(proveedoresA);
        Assert.Equal(cocaA.Id, proveedoresA[0].Id);

        // A no puede leer el de B por id → 404 (no 403).
        var cross = await clientA.GetAsync($"/proveedores/{pepsiB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Unicidad de nombre por negocio: A no puede repetir "Coca" → 409.
        var dup = await clientA.PostAsJsonAsync("/proveedores", new { nombre = "Coca" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // B sí puede usar "Coca" (otro negocio, otra unicidad).
        var cocaB = await clientB.PostAsJsonAsync("/proveedores", new { nombre = "Coca" });
        Assert.Equal(HttpStatusCode.Created, cocaB.StatusCode);

        // Email inválido → 400 (Data Annotation).
        var emailMalo = await clientA.PostAsJsonAsync("/proveedores", new { nombre = "Sprite", email = "no-es-email" });
        Assert.Equal(HttpStatusCode.BadRequest, emailMalo.StatusCode);
    }

    [Fact]
    public async Task Borrado_con_insumos_asignados_falla_y_baja_alta()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var prov = await CrearAsync(clientA, new { nombre = "Distribuidora Norte" });

        // Asignamos un insumo a ese proveedor (seed directo: el módulo de insumos no es necesario acá).
        var insumoId = Guid.NewGuid().ToString();
        await UsingDbAsync(async db =>
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            db.Insumos.Add(new Insumo
            {
                Id = insumoId,
                Nombre = "Harina",
                UnidadMedida = UnidadMedida.KILOGRAMO,
                StockActual = 10,
                StockMinimo = 1,
                Activo = true,
                ProveedorId = prov.Id,
                NegocioId = NegocioAId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        // El detalle del proveedor trae el insumo asignado.
        var detalle = await GetDetalleAsync(clientA, prov.Id);
        Assert.Single(detalle.Insumos);
        Assert.Equal("Harina", detalle.Insumos[0].Nombre);

        // Borrar con insumos asignados → 400 (guard aplicativo: la FK es SET NULL, la DB no bloquea).
        var bloqueado = await clientA.DeleteAsync($"/proveedores/{prov.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, bloqueado.StatusCode);

        // Desasignamos el insumo y reintentamos → 204. IgnoreQueryFilters: este scope no tiene tenant
        // resuelto (fail-closed), así que sin él el query filter ocultaría el insumo y no actualizaría nada.
        await UsingDbAsync(async db =>
        {
            await db.Insumos.IgnoreQueryFilters().Where(i => i.Id == insumoId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProveedorId, _ => null));
        });

        var borrado = await clientA.DeleteAsync($"/proveedores/{prov.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrado.StatusCode);

        var yaNoEsta = await clientA.GetAsync($"/proveedores/{prov.Id}");
        Assert.Equal(HttpStatusCode.NotFound, yaNoEsta.StatusCode);
    }

    [Fact]
    public async Task Baja_y_alta_logica_y_filtro_de_inactivos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var prov = await CrearAsync(clientA, new { nombre = "Mercado Central" });

        // Baja lógica.
        var baja = await clientA.PatchAsJsonAsync($"/proveedores/{prov.Id}/baja", new { });
        Assert.Equal(HttpStatusCode.OK, baja.StatusCode);

        // Por defecto el listado no incluye inactivos.
        var activos = await ListarAsync(clientA);
        Assert.DoesNotContain(activos, p => p.Id == prov.Id);

        // Con incluirInactivos=true sí aparece.
        var todos = await ListarAsync(clientA, incluirInactivos: true);
        Assert.Contains(todos, p => p.Id == prov.Id);

        // Alta de nuevo.
        var alta = await clientA.PatchAsJsonAsync($"/proveedores/{prov.Id}/alta", new { });
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        var activosOtraVez = await ListarAsync(clientA);
        Assert.Contains(activosOtraVez, p => p.Id == prov.Id);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task UsingDbAsync(Func<OrbitDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        await action(db);
    }

    private static async Task<ProveedorDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/proveedores", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProveedorDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ProveedorDto[]> ListarAsync(HttpClient client, bool incluirInactivos = false)
    {
        var url = incluirInactivos ? "/proveedores?incluirInactivos=true" : "/proveedores";
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<ProveedorDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static async Task<ProveedorDetalleDto> GetDetalleAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/proveedores/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProveedorDetalleDto>();
        Assert.NotNull(dto);
        return dto!;
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

    private sealed record ProveedorDto(
        string Id,
        string Nombre,
        string? Telefono,
        string? Email,
        string? Notas,
        bool Activo);

    private sealed record ProveedorDetalleDto(
        string Id,
        string Nombre,
        bool Activo,
        List<ProveedorInsumoDto> Insumos);

    private sealed record ProveedorInsumoDto(string Id, string Nombre, double StockActual, bool Activo);
}
