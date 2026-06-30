using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Insumos;

/// <summary>
/// Test de integración end-to-end del <c>InsumoController</c> (Tanda A) real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant (auth por cookie → claim negocioId → query filter);</item>
///   <item>validación estructural del <c>proveedorId</c> cross-tenant (400);</item>
///   <item>stock atómico (sumar/restar, insuficiente → 400) y registro de <c>StockMovimiento</c>;</item>
///   <item>ajuste de stock vía PATCH que registra un movimiento;</item>
///   <item>el guard de borrado por receta (400) y el borrado libre (204);</item>
///   <item>el endpoint público <c>disponibilidad-productos</c> por <c>?negocio=slug</c>;</item>
///   <item>roles: TRABAJADOR puede sumar pero no descontar (403).</item>
/// </list>
/// </summary>
[Collection(InsumoApiCollection.Name)]
public sealed class InsumosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-in-a";
    private const string NegocioBId = "neg-in-b";
    private const string NegocioASlug = "negocio-in-a";
    private const string NegocioBSlug = "negocio-in-b";
    private const string AdminAId = "user-in-a";
    private const string AdminBId = "user-in-b";
    private const string AdminAEmail = "admin-a@in.test";
    private const string AdminBEmail = "admin-b@in.test";
    private const string TrabajadorAId = "user-in-a-trab";
    private const string TrabajadorAEmail = "trab-a@in.test";

    private readonly InsumoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocio(db, NegocioAId, "Negocio IN A", NegocioASlug, now);
        SeedNegocio(db, NegocioBId, "Negocio IN B", NegocioBSlug, now);
        SeedUser(db, hasher, AdminAId, AdminAEmail, Role.ADMIN, NegocioAId, now);
        SeedUser(db, hasher, AdminBId, AdminBEmail, Role.ADMIN, NegocioBId, now);
        SeedUser(db, hasher, TrabajadorAId, TrabajadorAEmail, Role.TRABAJADOR, NegocioAId, now);
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
    public async Task Aislamiento_y_proveedor_cross_tenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        // La API bindea enums por valor numérico (igual que el resto de los controllers): System.Text.Json
        // serializa el enum como número, que es lo que el modelo de la API espera.
        var harinaA = await CrearAsync(clientA, new { nombre = "Harina", stockInicial = 10.0, unidadMedida = UnidadMedida.KILOGRAMO });
        Assert.Equal(10, harinaA.StockActual);
        Assert.Equal(5, harinaA.StockMinimo); // default DB
        Assert.True(harinaA.Activo);

        var aceiteB = await CrearAsync(clientB, new { nombre = "Aceite", stockInicial = 3.0 });

        // A sólo ve los suyos.
        var insumosA = await ListarAsync(clientA);
        Assert.Single(insumosA);
        Assert.Equal(harinaA.Id, insumosA[0].Id);

        // A no puede leer el de B → 404.
        var cross = await clientA.GetAsync($"/insumos/{aceiteB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Crear un proveedor en B y usar su id desde A → 400 (validación estructural de tenant).
        var provBId = Guid.NewGuid().ToString();
        await UsingDbAsync(async db =>
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            db.Proveedors.Add(new Proveedor { Id = provBId, Nombre = "Prov B", Activo = true, NegocioId = NegocioBId, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        });

        var conProvAjeno = await clientA.PostAsJsonAsync("/insumos", new { nombre = "Sal", stockInicial = 1.0, proveedorId = provBId });
        Assert.Equal(HttpStatusCode.BadRequest, conProvAjeno.StatusCode);
    }

    [Fact]
    public async Task Stock_atomico_y_movimientos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var insumo = await CrearAsync(clientA, new { nombre = "Queso", stockInicial = 5.0 });

        // El create NO registra movimiento (paridad NestJS).
        Assert.Equal(0, await ContarMovimientosAsync(insumo.Id));

        // Descontar más que el stock → 400 (sin tocar el stock).
        var insuf = await clientA.PatchAsJsonAsync($"/insumos/{insumo.Id}/restar", new { cantidad = 100.0 });
        Assert.Equal(HttpStatusCode.BadRequest, insuf.StatusCode);

        // Descontar 3 (5 → 2).
        var descontado = await PatchInsumoAsync(clientA, $"/insumos/{insumo.Id}/restar", new { cantidad = 3.0, motivo = "merma" });
        Assert.Equal(2, descontado.StockActual);

        // Sumar 8 (2 → 10).
        var sumado = await PatchInsumoAsync(clientA, $"/insumos/{insumo.Id}/sumar", new { cantidad = 8.0 });
        Assert.Equal(10, sumado.StockActual);

        // Ajuste vía PATCH (10 → 4) registra un movimiento adicional.
        var ajustado = await PatchInsumoAsync(clientA, $"/insumos/{insumo.Id}", new { stockActual = 4.0 });
        Assert.Equal(4, ajustado.StockActual);

        // Total: 3 movimientos (restar, sumar, ajuste PATCH), todos AJUSTE_MANUAL con UserId del admin.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var movimientos = await db.StockMovimientos.IgnoreQueryFilters()
            .Where(m => m.InsumoId == insumo.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Cantidad)
            .ToListAsync();

        Assert.Equal(3, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal("AJUSTE_MANUAL", m.Tipo));
        Assert.All(movimientos, m => Assert.Equal(NegocioAId, m.NegocioId));
        Assert.All(movimientos, m => Assert.Equal(AdminAId, m.UserId));

        // El endpoint paginado de movimientos los devuelve.
        var pagina = await GetMovimientosPaginadosAsync(clientA, insumo.Id);
        Assert.Equal(3, pagina.Total);
        Assert.Equal(3, pagina.Data.Count);
    }

    [Fact]
    public async Task Borrado_con_receta_falla_y_borrado_libre_ok()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var enReceta = await CrearAsync(clientA, new { nombre = "Tomate", stockInicial = 20.0 });
        var libre = await CrearAsync(clientA, new { nombre = "Albahaca", stockInicial = 5.0 });

        // Seed de un producto con receta que usa "Tomate".
        await UsingDbAsync(async db =>
        {
            var productoId = Guid.NewGuid().ToString();
            db.Productos.Add(new Producto { Id = productoId, Nombre = "Salsa", NegocioId = NegocioAId, EsParaVenta = true, Activo = true });
            db.ProductoReceta.Add(new ProductoRecetum
            {
                Id = Guid.NewGuid().ToString(),
                ProductoId = productoId,
                InsumoId = enReceta.Id,
                Cantidad = 2,
                NegocioId = NegocioAId,
            });
            await db.SaveChangesAsync();
        });

        // Borrar el insumo en receta → 400.
        var bloqueado = await clientA.DeleteAsync($"/insumos/{enReceta.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, bloqueado.StatusCode);

        // Borrar el insumo libre → 204.
        var borrado = await clientA.DeleteAsync($"/insumos/{libre.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrado.StatusCode);
    }

    [Fact]
    public async Task Disponibilidad_productos_publica_por_slug()
    {
        // Seed: insumo con stock 10; producto con receta liviana (disponible), pesada (no), y sin receta.
        string conStockId = Guid.NewGuid().ToString();
        string sinRecetaId = Guid.NewGuid().ToString();
        string pesadoId = Guid.NewGuid().ToString();
        await UsingDbAsync(async db =>
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            var insumoId = Guid.NewGuid().ToString();
            db.Insumos.Add(new Insumo { Id = insumoId, Nombre = "Pan", UnidadMedida = UnidadMedida.UNIDAD, StockActual = 10, StockMinimo = 1, Activo = true, NegocioId = NegocioAId, CreatedAt = now, UpdatedAt = now });

            db.Productos.Add(new Producto { Id = conStockId, Nombre = "Liviano", NegocioId = NegocioAId, EsParaVenta = true, Activo = true });
            db.Productos.Add(new Producto { Id = pesadoId, Nombre = "Pesado", NegocioId = NegocioAId, EsParaVenta = true, Activo = true });
            db.Productos.Add(new Producto { Id = sinRecetaId, Nombre = "SinReceta", NegocioId = NegocioAId, EsParaVenta = true, Activo = true });

            db.ProductoReceta.Add(new ProductoRecetum { Id = Guid.NewGuid().ToString(), ProductoId = conStockId, InsumoId = insumoId, Cantidad = 5, NegocioId = NegocioAId });
            db.ProductoReceta.Add(new ProductoRecetum { Id = Guid.NewGuid().ToString(), ProductoId = pesadoId, InsumoId = insumoId, Cantidad = 50, NegocioId = NegocioAId });
            await db.SaveChangesAsync();
        });

        var anon = _factory.CreateClient();

        // Sin slug → 400.
        var sinSlug = await anon.GetAsync("/insumos/disponibilidad-productos");
        Assert.Equal(HttpStatusCode.BadRequest, sinSlug.StatusCode);

        // Slug inexistente → 404.
        var slugMalo = await anon.GetAsync("/insumos/disponibilidad-productos?negocio=no-existe");
        Assert.Equal(HttpStatusCode.NotFound, slugMalo.StatusCode);

        // Slug válido → 200 con los 3 productos del negocio A.
        var response = await anon.GetAsync($"/insumos/disponibilidad-productos?negocio={NegocioASlug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disponibilidad = await response.Content.ReadFromJsonAsync<DisponibilidadDto[]>();
        Assert.NotNull(disponibilidad);

        Assert.True(disponibilidad!.Single(d => d.ProductoId == conStockId).Disponible);
        Assert.False(disponibilidad.Single(d => d.ProductoId == pesadoId).Disponible);
        Assert.True(disponibilidad.Single(d => d.ProductoId == sinRecetaId).Disponible);
    }

    [Fact]
    public async Task Roles_trabajador_puede_sumar_no_descontar()
    {
        var clientAdmin = await LoginAsync(AdminAEmail, NegocioASlug);
        var insumo = await CrearAsync(clientAdmin, new { nombre = "Lechuga", stockInicial = 10.0 });

        var clientTrab = await LoginAsync(TrabajadorAEmail, NegocioASlug);

        // TRABAJADOR puede sumar.
        var sumar = await clientTrab.PatchAsJsonAsync($"/insumos/{insumo.Id}/sumar", new { cantidad = 5.0 });
        Assert.Equal(HttpStatusCode.OK, sumar.StatusCode);

        // TRABAJADOR NO puede descontar (ADMIN-only) → 403.
        var restar = await clientTrab.PatchAsJsonAsync($"/insumos/{insumo.Id}/restar", new { cantidad = 1.0 });
        Assert.Equal(HttpStatusCode.Forbidden, restar.StatusCode);

        // TRABAJADOR NO puede crear (ADMIN-only) → 403.
        var crear = await clientTrab.PostAsJsonAsync("/insumos", new { nombre = "Cebolla", stockInicial = 1.0 });
        Assert.Equal(HttpStatusCode.Forbidden, crear.StatusCode);
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

    private async Task<int> ContarMovimientosAsync(string insumoId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return await db.StockMovimientos.IgnoreQueryFilters().CountAsync(m => m.InsumoId == insumoId);
    }

    private static async Task<InsumoDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/insumos", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<InsumoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<InsumoDto[]> ListarAsync(HttpClient client)
    {
        var response = await client.GetAsync("/insumos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<InsumoDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static async Task<InsumoDto> PatchInsumoAsync(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<InsumoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<PagedDto> GetMovimientosPaginadosAsync(HttpClient client, string insumoId)
    {
        var response = await client.GetAsync($"/insumos/{insumoId}/movimientos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PagedDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocio(OrbitDbContext db, string negocioId, string nombre, string slug, DateTime now)
    {
        db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = nombre,
            Slug = slug,
            Activo = true,
            Plan = "basic",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static void SeedUser(
        OrbitDbContext db, IPasswordHasher hasher,
        string userId, string email, Role role, string negocioId, DateTime now)
    {
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            Password = hasher.Hash(Password),
            Nombre = role.ToString(),
            Role = role,
            Activo = true,
            NegocioId = negocioId,
            EmailVerificado = true,
            CreatedAt = now,
        });
    }

    private sealed record InsumoDto(
        string Id,
        string Nombre,
        double StockActual,
        double StockMinimo,
        bool Activo,
        string? ProveedorId,
        string? ProveedorNombre);

    private sealed record DisponibilidadDto(string ProductoId, bool Disponible);

    private sealed record PagedDto(List<MovimientoDto> Data, int Total, int Page, int TotalPages);

    private sealed record MovimientoDto(string Id, string? InsumoId, string Tipo, double Cantidad, double StockAntes, double StockDespues);
}
