using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Productos;

/// <summary>
/// Test de integración end-to-end del <c>ProductoController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant + menú público básico por <c>?negocio=slug</c> (sin receta);</item>
///   <item>hardening: el menú completo (con receta) exige autenticación → anónimo recibe 401;</item>
///   <item>cross-tenant: crear con <c>categoriaId</c> ajeno → 400 'Categoría inválida' (paridad NestJS);</item>
///   <item>código duplicado → 409; borrado bloqueado por uso en pedidos → 409;</item>
///   <item>receta tipada (insumo × cantidad) y auditoría CAMBIO_PRECIO con userId.</item>
/// </list>
/// </summary>
[Collection(ProductoApiCollection.Name)]
public sealed class ProductosIsolationTests : IAsyncLifetime
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
    private const string CategoriaAId = "cat-pr-a";
    private const string CategoriaBId = "cat-pr-b";
    private const string InsumoAId = "ins-pr-a";

    private readonly ProductoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocio(db, hasher, NegocioAId, "Negocio PR A", NegocioASlug, AdminAId, AdminAEmail, CategoriaAId, now);
        SeedNegocio(db, hasher, NegocioBId, "Negocio PR B", NegocioBSlug, AdminBId, AdminBEmail, CategoriaBId, now);

        db.Insumos.Add(new Insumo
        {
            Id = InsumoAId,
            Nombre = "Harina",
            UnidadMedida = UnidadMedida.GRAMO,
            StockActual = 1000,
            StockMinimo = 0,
            Activo = true,
            NegocioId = NegocioAId,
            CreatedAt = now,
            UpdatedAt = now,
        });

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
    public async Task Aislamiento_menu_publico_basico_y_hardening_del_completo()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        // Producto de A con receta + toppingGruposCompatibles; producto de B sin receta.
        var pizzaA = await CrearProductoAsync(clientA, new
        {
            nombre = "Muzzarella",
            precio = 1200.0,
            categoriaId = CategoriaAId,
            codigo = "PZ-001",
            toppingGruposCompatibles = new[] { "grupo-x", "grupo-y" },
            receta = new[] { new { insumoId = InsumoAId, cantidad = 250.0 } },
        });
        Assert.True(pizzaA.Activo);
        Assert.Equal(new[] { "grupo-x", "grupo-y" }, pizzaA.ToppingGruposCompatibles);
        Assert.Single(pizzaA.Receta);
        Assert.Equal(InsumoAId, pizzaA.Receta[0].InsumoId);
        Assert.Equal(250.0, pizzaA.Receta[0].Cantidad);
        Assert.Equal("Harina", pizzaA.Receta[0].Insumo.Nombre);

        var aguaB = await CrearProductoAsync(clientB, new { nombre = "Agua", precio = 300.0, categoriaId = CategoriaBId });

        // Cross-tenant GET por id → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/productos/{aguaB.Id}")).StatusCode);

        // ── Menú PÚBLICO básico de A por slug (anónimo): sin receta, con TieneReceta ───────────────
        var anon = _factory.CreateClient();
        var basicos = await GetBasicosAsync(anon, $"/productos?basico=true&negocio={NegocioASlug}");
        Assert.Single(basicos);
        Assert.Equal(pizzaA.Id, basicos[0].Id);
        Assert.True(basicos[0].TieneReceta);
        Assert.Equal(new[] { "grupo-x", "grupo-y" }, basicos[0].ToppingGruposCompatibles);
        Assert.DoesNotContain(basicos, p => p.Id == aguaB.Id);

        // ── Hardening: el menú COMPLETO (con receta) NO es público → 401 anónimo ───────────────────
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/productos?negocio={NegocioASlug}")).StatusCode);

        // Autenticado sí ve el menú completo con receta.
        var completos = await GetCompletosAsync(clientA, "/productos");
        Assert.Single(completos);
        Assert.Single(completos[0].Receta);

        // Sin parámetro 'negocio' (básico anónimo) → 400; slug inexistente → 404.
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/productos?basico=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/productos?basico=true&negocio=no-existe")).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_categoria_codigo_duplicado_y_validacion_insumo()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // categoriaId de OTRO negocio → 400 'Categoría inválida' (paridad NestJS).
        Assert.Equal(HttpStatusCode.BadRequest, (await clientA.PostAsJsonAsync("/productos",
            new { nombre = "Fugazza", precio = 1000.0, categoriaId = CategoriaBId })).StatusCode);

        // insumo de receta inexistente/ajeno → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await clientA.PostAsJsonAsync("/productos", new
        {
            nombre = "Napolitana",
            precio = 1000.0,
            categoriaId = CategoriaAId,
            receta = new[] { new { insumoId = "ins-inexistente", cantidad = 10.0 } },
        })).StatusCode);

        // codigo duplicado dentro del mismo negocio → 409.
        await CrearProductoAsync(clientA, new { nombre = "Calabresa", precio = 1000.0, categoriaId = CategoriaAId, codigo = "DUP-1" });
        Assert.Equal(HttpStatusCode.Conflict, (await clientA.PostAsJsonAsync("/productos",
            new { nombre = "Otra", precio = 1000.0, categoriaId = CategoriaAId, codigo = "DUP-1" })).StatusCode);
    }

    [Fact]
    public async Task Update_audita_cambio_de_precio_y_reemplaza_receta()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var prod = await CrearProductoAsync(clientA, new
        {
            nombre = "Especial",
            precio = 1000.0,
            categoriaId = CategoriaAId,
            receta = new[] { new { insumoId = InsumoAId, cantidad = 100.0 } },
        });

        // PATCH: cambia precio (audita) y reemplaza la receta por una vacía.
        var actualizado = await PatchProductoAsync(clientA, $"/productos/{prod.Id}",
            new { precio = 1500.0, receta = Array.Empty<object>() });
        Assert.Equal(1500.0, actualizado.Precio);
        Assert.Empty(actualizado.Receta);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        // La receta vieja se borró (reemplazo total).
        var recetaCount = await db.ProductoReceta.IgnoreQueryFilters().CountAsync(r => r.ProductoId == prod.Id);
        Assert.Equal(0, recetaCount);

        // Se registró el CAMBIO_PRECIO con entidad/entidadId/userId correctos.
        var audit = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entidad == "Producto" && a.EntidadId == prod.Id && a.Accion == "CAMBIO_PRECIO")
            .ToListAsync();
        Assert.Single(audit);
        Assert.Equal(NegocioAId, audit[0].NegocioId);
        Assert.Equal(AdminAId, audit[0].UsuarioId);
        Assert.NotNull(audit[0].Detalle);
        Assert.Contains("1000", audit[0].Detalle);
        Assert.Contains("1500", audit[0].Detalle);
    }

    [Fact]
    public async Task Delete_bloqueado_si_usado_en_pedidos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var usado = await CrearProductoAsync(clientA, new { nombre = "Vendido", precio = 900.0, categoriaId = CategoriaAId });
        var libre = await CrearProductoAsync(clientA, new { nombre = "Sin ventas", precio = 900.0, categoriaId = CategoriaAId });

        // Sembrar un pedido con un detalle que referencia al producto "usado".
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            db.Pedidos.Add(new Pedido
            {
                Id = "ped-pr-1",
                Tipo = TipoPedido.RETIRO,
                Estado = EstadoPedido.PENDIENTE,
                EstadoPago = EstadoPago.PENDIENTE,
                Total = 900,
                NegocioId = NegocioAId,
                CreatedAt = now,
            });
            db.PedidoDetalles.Add(new PedidoDetalle
            {
                Id = "pd-pr-1",
                PedidoId = "ped-pr-1",
                ProductoId = usado.Id,
                Cantidad = 1,
                Subtotal = 900,
                PrecioUnitario = 900,
                NegocioId = NegocioAId,
            });
            await db.SaveChangesAsync();
        }

        // Producto usado en pedidos → 409; producto libre → 204.
        Assert.Equal(HttpStatusCode.Conflict, (await clientA.DeleteAsync($"/productos/{usado.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await clientA.DeleteAsync($"/productos/{libre.Id}")).StatusCode);
    }

    [Fact]
    public async Task MasVendidos_rankea_por_unidades_y_cae_a_aleatorio_sin_ventas()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var pocoVendido = await CrearProductoAsync(clientA, new { nombre = "Poco", precio = 100.0, categoriaId = CategoriaAId });
        var muyVendido = await CrearProductoAsync(clientA, new { nombre = "Mucho", precio = 100.0, categoriaId = CategoriaAId });

        var anon = _factory.CreateClient();

        // Sin ventas → fallback: devuelve activos (2) sin romper.
        var fallback = await GetBasicosAsync(anon, $"/productos/mas-vendidos?negocio={NegocioASlug}");
        Assert.Equal(2, fallback.Length);

        // Sembrar ventas: 5 unidades de "Mucho" vs 1 de "Poco".
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            db.Pedidos.Add(new Pedido
            {
                Id = "ped-mv-1",
                Tipo = TipoPedido.RETIRO,
                Estado = EstadoPedido.PENDIENTE,
                EstadoPago = EstadoPago.PENDIENTE,
                Total = 600,
                NegocioId = NegocioAId,
                CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            });
            db.PedidoDetalles.Add(new PedidoDetalle
            {
                Id = "pd-mv-1", PedidoId = "ped-mv-1", ProductoId = muyVendido.Id,
                Cantidad = 5, Subtotal = 500, PrecioUnitario = 100, NegocioId = NegocioAId,
            });
            db.PedidoDetalles.Add(new PedidoDetalle
            {
                Id = "pd-mv-2", PedidoId = "ped-mv-1", ProductoId = pocoVendido.Id,
                Cantidad = 1, Subtotal = 100, PrecioUnitario = 100, NegocioId = NegocioAId,
            });
            await db.SaveChangesAsync();
        }

        var ranking = await GetBasicosAsync(anon, $"/productos/mas-vendidos?negocio={NegocioASlug}");
        Assert.Equal(muyVendido.Id, ranking[0].Id);
        Assert.Equal(pocoVendido.Id, ranking[1].Id);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ProductoDto> CrearProductoAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/productos", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProductoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ProductoDto> PatchProductoAsync(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProductoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ProductoDto[]> GetCompletosAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<ProductoDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static async Task<ProductoBasicoDto[]> GetBasicosAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<ProductoBasicoDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static void SeedNegocio(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, string categoriaId, DateTime now)
    {
        db.Negocios.Add(new Negocio
        {
            Id = negocioId, Nombre = negocioNombre, Slug = slug, Activo = true,
            Plan = "basic", CreatedAt = now, UpdatedAt = now,
        });
        db.Users.Add(new User
        {
            Id = userId, Email = email, Password = hasher.Hash(Password), Nombre = "Admin",
            Role = Role.ADMIN, Activo = true, NegocioId = negocioId, EmailVerificado = true, CreatedAt = now,
        });
        db.Categoria.Add(new Categorium
        {
            Id = categoriaId, Nombre = "Pizzas", Activo = true, Orden = 0,
            MaxAderezosGratis = 2, NegocioId = negocioId, CreatedAt = now, UpdatedAt = now,
        });
    }

    private sealed record ProductoBasicoDto(
        string Id,
        string Nombre,
        double Precio,
        bool Activo,
        List<string> ToppingGruposCompatibles,
        string? CategoriaId,
        bool TieneReceta);

    private sealed record ProductoDto(
        string Id,
        string Nombre,
        double Precio,
        bool Activo,
        string? Codigo,
        List<string> ToppingGruposCompatibles,
        string? CategoriaId,
        List<RecetaItemDto> Receta);

    private sealed record RecetaItemDto(string InsumoId, double Cantidad, InsumoRefDto Insumo);

    private sealed record InsumoRefDto(string Id, string Nombre, UnidadMedida UnidadMedida);
}
