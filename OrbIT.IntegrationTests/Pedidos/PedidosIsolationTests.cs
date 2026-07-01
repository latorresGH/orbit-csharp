using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Pedidos;

/// <summary>
/// Test de integración end-to-end del módulo Pedidos (Tanda A). El fact estrella es
/// <see cref="Create_LOCAL_completo_flujo_end_to_end"/>: primer test que verifica el flujo completo de
/// punta a punta (stock, pricing de extras/aderezos, upsert de cliente, StockMovimiento) en una sola
/// transacción. Si ese pasa, el núcleo del sistema está sólido.
/// </summary>
[Collection(PedidoApiCollection.Name)]
public sealed class PedidosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-pe-a";
    private const string NegocioBId = "neg-pe-b";
    private const string NegocioASlug = "negocio-pe-a";
    private const string NegocioBSlug = "negocio-pe-b";
    private const string AdminAEmail = "admin-a@pe.test";
    private const string AdminBEmail = "admin-b@pe.test";

    private const string CatId = "cat-pe";
    private const string InsId = "ins-pe";
    private const string ProdRecetaId = "prod-pe-receta";   // precio 1000, categoría CatId, receta 2×InsId
    private const string SimpleProdId = "prod-pe-simple";   // precio 500, sin categoría ni receta
    private const string TgId = "tg-pe";                    // maxExtrasGratis=1, esIncluido
    private const string ExtraId = "extra-pe";              // precio 200, stock 50, consumo 1
    private const string AdeId = "ade-pe";                  // precio 150, stock 30, consumo 1
    private const string ProdBId = "prod-pe-b";

    private readonly PedidoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio PE A", NegocioASlug, "user-pe-a", AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio PE B", NegocioBSlug, "user-pe-b", AdminBEmail, now);

        db.Categoria.Add(new Categorium { Id = CatId, Nombre = "Pizzas", Activo = true, Orden = 0, MaxAderezosGratis = 0, NegocioId = NegocioAId, CreatedAt = now, UpdatedAt = now });
        db.Insumos.Add(new Insumo { Id = InsId, Nombre = "Harina", UnidadMedida = UnidadMedida.KILOGRAMO, StockActual = 100, StockMinimo = 1, Activo = true, NegocioId = NegocioAId, CreatedAt = now, UpdatedAt = now });
        db.Productos.Add(new Producto { Id = ProdRecetaId, Nombre = "Muzza", Precio = 1000, Activo = true, CategoriaId = CatId, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = SimpleProdId, Nombre = "Agua", Precio = 500, Activo = true, NegocioId = NegocioAId });
        db.ProductoReceta.Add(new ProductoRecetum { Id = "rec-pe", ProductoId = ProdRecetaId, InsumoId = InsId, Cantidad = 2, NegocioId = NegocioAId });
        db.ToppingGrupos.Add(new ToppingGrupo { Id = TgId, Nombre = "Toppings", MaxExtrasGratis = 1, EsIncluido = true, Orden = 0, Activo = true, NegocioId = NegocioAId });
        db.Extras.Add(new Extra { Id = ExtraId, Nombre = "Cheddar", UnidadMedida = UnidadMedida.UNIDAD, Precio = 200, StockActual = 50, Activo = true, Categoria = "TOPPINGS", EsPremium = false, ToppingGrupoId = TgId, NegocioId = NegocioAId, CreatedAt = now, UpdatedAt = now });
        db.ExtraConsumos.Add(new ExtraConsumo { Id = "ec-pe", ExtraId = ExtraId, CategoriaId = CatId, CantidadConsumo = 1, NegocioId = NegocioAId });
        // EsPremium=true: el aderezo siempre se cobra (independiente de maxAderezosGratis). Necesario porque
        // MaxAderezosGratis=0 NO es seedable (0 = sentinel de EF → toma el default DB 2). Ver [[ef-enum-sentinel-warnings]].
        db.Aderezos.Add(new Aderezo { Id = AdeId, Nombre = "Ketchup", UnidadMedida = UnidadMedida.MILILITRO, Activo = true, StockActual = 30, EsPremium = true, Precio = 150, NegocioId = NegocioAId });
        db.AderezoConsumos.Add(new AderezoConsumo { Id = "ac-pe", AderezoId = AdeId, CategoriaId = CatId, CantidadConsumo = 1, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = ProdBId, Nombre = "Producto B", Precio = 500, Activo = true, NegocioId = NegocioBId });

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
    public async Task Create_LOCAL_completo_flujo_end_to_end()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var resp = await client.PostAsJsonAsync("/pedidos", new
        {
            tipo = TipoPedido.LOCAL,
            nombreCliente = "Juan",
            apellidoCliente = "Pérez",
            numeroCliente = "1122334455",
            detalles = new[]
            {
                new
                {
                    productoId = ProdRecetaId,
                    cantidad = 1,
                    extras = new[] { new { extraId = ExtraId, cantidad = 2 } },
                    aderezosIds = new[] { AdeId },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var pedido = (await resp.Content.ReadFromJsonAsync<PedidoDto>())!;

        // Total = producto 1000 + extra cobrado 200 (1 de 2 unidades, la otra gratis) + aderezo 150 (cat sin gratis).
        Assert.Equal(1350, pedido.Total);
        Assert.NotNull(pedido.ClienteId);

        var detalle = Assert.Single(pedido.Detalles);
        Assert.Equal(2, detalle.Extras.Count);
        Assert.Equal(1, detalle.Extras.Count(e => !e.Cobrado)); // una unidad gratis
        Assert.Equal(1, detalle.Extras.Count(e => e.Cobrado));  // una cobrada
        Assert.Single(detalle.Aderezos);

        await UsingDbAsync(async db =>
        {
            // Stock descontado.
            Assert.Equal(98, (await db.Insumos.IgnoreQueryFilters().FirstAsync(i => i.Id == InsId)).StockActual);
            Assert.Equal(48, (await db.Extras.IgnoreQueryFilters().FirstAsync(e => e.Id == ExtraId)).StockActual);
            Assert.Equal(29, (await db.Aderezos.IgnoreQueryFilters().FirstAsync(a => a.Id == AdeId)).StockActual);

            // Movimientos de stock del pedido.
            var movs = await db.StockMovimientos.IgnoreQueryFilters().Where(m => m.PedidoId == pedido.Id).ToListAsync();
            Assert.Equal(3, movs.Count);
            Assert.All(movs, m => Assert.Equal("DESCUENTO_PEDIDO", m.Tipo));

            // Cliente upserteado con totales (la mejora aprobada).
            var cliente = await db.Clientes.IgnoreQueryFilters().FirstAsync(c => c.Telefono == "1122334455");
            Assert.Equal(1, cliente.TotalPedidos);
            Assert.Equal(1350, cliente.TotalGastado);
            Assert.NotNull(cliente.FechaUltimoPedido);
            Assert.Equal(cliente.Id, pedido.ClienteId);
        });
    }

    [Fact]
    public async Task Create_stock_insuficiente_revierte_todo()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        // 60 unidades × receta 2 = 120 de insumo > 100 disponible → 400, sin persistir nada.
        var resp = await client.PostAsJsonAsync("/pedidos", new
        {
            tipo = TipoPedido.LOCAL,
            nombreCliente = "Ana",
            apellidoCliente = "García",
            numeroCliente = "1199887766",
            detalles = new[] { new { productoId = ProdRecetaId, cantidad = 60 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        await UsingDbAsync(async db =>
        {
            Assert.Equal(0, await db.Pedidos.IgnoreQueryFilters().CountAsync());
            Assert.Equal(100, (await db.Insumos.IgnoreQueryFilters().FirstAsync(i => i.Id == InsId)).StockActual);
        });
    }

    [Fact]
    public async Task Transiciones_estado_base_y_override_delivery()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var local = await CrearSimpleAsync(client, TipoPedido.LOCAL);

        // Base: PENDIENTE → EN_PREPARACION (válido).
        var ok = await client.PatchAsJsonAsync($"/pedidos/{local}/estado", new { estado = EstadoPedido.EN_PREPARACION });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Base: EN_PREPARACION → ENTREGADO (inválido, no está en el grafo).
        var malo = await client.PatchAsJsonAsync($"/pedidos/{local}/estado", new { estado = EstadoPedido.ENTREGADO });
        Assert.Equal(HttpStatusCode.BadRequest, malo.StatusCode);

        // Override DELIVERY: PENDIENTE → EN_CAMINO (válido en delivery, NO en el grafo base).
        var delivery = await CrearSimpleAsync(client, TipoPedido.DELIVERY, direccion: "Calle Falsa 123");
        var enCamino = await client.PatchAsJsonAsync($"/pedidos/{delivery}/estado", new { estado = EstadoPedido.EN_CAMINO });
        Assert.Equal(HttpStatusCode.OK, enCamino.StatusCode);
    }

    [Fact]
    public async Task Cancelar_restaura_stock_y_marca_cancelado()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var resp = await client.PostAsJsonAsync("/pedidos", new
        {
            tipo = TipoPedido.LOCAL,
            nombreCliente = "Leo",
            apellidoCliente = "Díaz",
            numeroCliente = "1100110011",
            detalles = new[]
            {
                new { productoId = ProdRecetaId, cantidad = 1, extras = new[] { new { extraId = ExtraId, cantidad = 1 } }, aderezosIds = new[] { AdeId } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var pedido = (await resp.Content.ReadFromJsonAsync<PedidoDto>())!;

        var cancel = await client.PostAsJsonAsync($"/pedidos/{pedido.Id}/cancelar", new { motivo = "Cliente se arrepintió" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelado = (await cancel.Content.ReadFromJsonAsync<PedidoDto>())!;
        Assert.Equal(EstadoPedido.CANCELADO, cancelado.Estado);

        await UsingDbAsync(async db =>
        {
            // Stock restaurado a los valores iniciales.
            Assert.Equal(100, (await db.Insumos.IgnoreQueryFilters().FirstAsync(i => i.Id == InsId)).StockActual);
            Assert.Equal(50, (await db.Extras.IgnoreQueryFilters().FirstAsync(e => e.Id == ExtraId)).StockActual);
            Assert.Equal(30, (await db.Aderezos.IgnoreQueryFilters().FirstAsync(a => a.Id == AdeId)).StockActual);

            var devoluciones = await db.StockMovimientos.IgnoreQueryFilters()
                .Where(m => m.PedidoId == pedido.Id && m.Tipo == "DEVOLUCION_CANCELACION").CountAsync();
            Assert.Equal(3, devoluciones);
        });
    }

    [Fact]
    public async Task Tracking_publico_sin_tenant()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var id = await CrearSimpleAsync(client, TipoPedido.LOCAL);

        // Cliente anónimo, sin login ni slug — id opaco.
        var anon = _factory.CreateClient();
        var tracking = await anon.GetFromJsonAsync<TrackingDto>($"/pedidos/{id}/tracking");
        Assert.Equal(id, tracking!.Id);
        Assert.Equal(EstadoPedido.PENDIENTE, tracking.Estado);
        Assert.Null(tracking.Direccion); // LOCAL no expone dirección
    }

    [Fact]
    public async Task Aislamiento_multitenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var id = await CrearSimpleAsync(clientA, TipoPedido.LOCAL);

        // B no puede leer el pedido de A.
        var cross = await clientB.GetAsync($"/pedidos/{id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // A lo ve en su listado; B no.
        var listaA = await clientA.GetFromJsonAsync<PedidoDto[]>("/pedidos");
        Assert.Contains(listaA!, p => p.Id == id);
        var listaB = await clientB.GetFromJsonAsync<PedidoDto[]>("/pedidos");
        Assert.DoesNotContain(listaB!, p => p.Id == id);
    }

    [Fact]
    public async Task Cuenta_abierta_listar_cerrar_y_anular()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var cuenta = await CrearSimpleAsync(client, TipoPedido.LOCAL, cuentaAbierta: true);
        var aAnular = await CrearSimpleAsync(client, TipoPedido.LOCAL, cuentaAbierta: true);

        // Aparecen como cuentas abiertas.
        var abiertas = await client.GetFromJsonAsync<PedidoDto[]>("/pedidos/cuentas-abiertas");
        Assert.Contains(abiertas!, p => p.Id == cuenta);
        Assert.Contains(abiertas!, p => p.Id == aAnular);

        // Cerrar una.
        var cerrar = await client.PatchAsJsonAsync($"/pedidos/{cuenta}/cerrar-cuenta", new { });
        Assert.Equal(HttpStatusCode.OK, cerrar.StatusCode);

        // Anular la otra.
        var anular = await client.PostAsJsonAsync($"/pedidos/{aAnular}/anular", new { motivo = "Error de carga" });
        Assert.Equal(HttpStatusCode.OK, anular.StatusCode);
        var anulado = (await anular.Content.ReadFromJsonAsync<PedidoDto>())!;
        Assert.Equal(EstadoPago.ANULADO, anulado.EstadoPago);

        // Ya no quedan cuentas abiertas.
        var luego = await client.GetFromJsonAsync<PedidoDto[]>("/pedidos/cuentas-abiertas");
        Assert.DoesNotContain(luego!, p => p.Id == cuenta);
        Assert.DoesNotContain(luego!, p => p.Id == aAnular);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<string> CrearSimpleAsync(HttpClient client, TipoPedido tipo, bool cuentaAbierta = false, string? direccion = null)
    {
        var resp = await client.PostAsJsonAsync("/pedidos", new
        {
            tipo,
            nombreCliente = "Test",
            apellidoCliente = "Cliente",
            numeroCliente = "1234567890",
            cuentaAbierta,
            direccion,
            detalles = new[] { new { productoId = SimpleProdId, cantidad = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var pedido = (await resp.Content.ReadFromJsonAsync<PedidoDto>())!;
        return pedido.Id;
    }

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

    private static void SeedNegocioConAdmin(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, DateTime now)
    {
        db.Negocios.Add(new Negocio { Id = negocioId, Nombre = negocioNombre, Slug = slug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = userId, Email = email, Password = hasher.Hash(Password), Nombre = "Admin", Role = Role.ADMIN, Activo = true, NegocioId = negocioId, EmailVerificado = true, CreatedAt = now });
    }

    private sealed record PedidoDto(
        string Id, double Total, EstadoPedido Estado, EstadoPago EstadoPago,
        string? ClienteId, string? MesaId, bool CuentaAbierta, List<DetalleDto> Detalles);

    private sealed record DetalleDto(string Id, double Subtotal, List<ExtraDto> Extras, List<AderezoDto> Aderezos);

    private sealed record ExtraDto(string Id, string Nombre, double Precio, bool Cobrado);

    private sealed record AderezoDto(string Id, string Nombre);

    private sealed record TrackingDto(string Id, EstadoPedido Estado, double Total, string? NombreCliente, string? Direccion);
}
