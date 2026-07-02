using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Dashboard;

/// <summary>
/// Tests de integración del Dashboard (solo ADMIN). Verifica <c>/dashboard/metrics</c> (agregación server-side
/// de totales, corte por método de pago, por tipo, clientes period-scoped y comparativa contra el período
/// anterior) y <c>/dashboard/resumen-hoy</c> (datos en vivo con y sin turno activo), más validación de rango y
/// aislamiento multi-tenant. El baseline (negocios/usuarios/producto) se siembra una vez; cada test siembra sus
/// propios pedidos para no contaminar los conteos "en vivo" de resumen-hoy.
/// </summary>
[Collection(DashboardApiCollection.Name)]
public sealed class DashboardTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-dash-a";
    private const string NegocioBId = "neg-dash-b";
    private const string NegocioASlug = "negocio-dash-a";
    private const string NegocioBSlug = "negocio-dash-b";
    private const string AdminAId = "user-dash-a";
    private const string AdminAEmail = "admin-a@dash.test";
    private const string TrabajadorAEmail = "trab-a@dash.test";
    private const string AdminBEmail = "admin-b@dash.test";
    private const string ProdAId = "prod-dash-a";
    private const string ProdBId = "prod-dash-b";

    private readonly DashboardWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio Dash A", Slug = NegocioASlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Negocios.Add(new Negocio { Id = NegocioBId, Nombre = "Negocio Dash B", Slug = NegocioBSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = AdminAId, Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-dash-a-trab", Email = TrabajadorAEmail, Password = hasher.Hash(Password), Nombre = "Trabajador A", Role = Role.TRABAJADOR, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-dash-b", Email = AdminBEmail, Password = hasher.Hash(Password), Nombre = "Admin B", Role = Role.ADMIN, Activo = true, NegocioId = NegocioBId, EmailVerificado = true, CreatedAt = now });
        db.Productos.Add(new Producto { Id = ProdAId, Nombre = "Pizza", Precio = 1000, Activo = true, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = ProdBId, Nombre = "Empanada", Precio = 500, Activo = true, NegocioId = NegocioBId });

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

    // ═══════════════════════════════════════════════════════════════════════════
    // METRICS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Metrics_agrega_totales_pagos_tipos_clientes_y_comparativa()
    {
        // Mediodía UTC (15:00) = 12:00 AR: seguro dentro del mismo día AR sin importar el offset -03:00.
        DateTime Jun(int day) => new(2026, 6, day, 15, 0, 0);

        await UsingDbAsync(async db =>
        {
            // ── Período actual (junio 2026), sólo ENTREGADO cuenta en la parte financiera ──
            // m1: LOCAL / EFECTIVO / 1000 / cliente 111 / 10-jun / Pizza x2 (subtotal 2000)
            var m1 = Pedido("dm1", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 1000, 0, Jun(10), "111");
            m1.PedidoDetalles.Add(Detalle("dd1", m1.Id, 2, 2000, ProdAId));
            // m2: DELIVERY / TRANSFERENCIA / 1500 / envío 300 / cliente 222 / 10-jun / Pizza x1 (1000)
            var m2 = Pedido("dm2", TipoPedido.DELIVERY, EstadoPedido.ENTREGADO, MetodoPago.TRANSFERENCIA, 1500, 300, Jun(10), "222");
            m2.PedidoDetalles.Add(Detalle("dd2", m2.Id, 1, 1000, ProdAId));
            // m3: DELIVERY / TARJETA / 500 / envío 200 / cliente 111 (recurrente) / 11-jun / Pizza x1 (500)
            var m3 = Pedido("dm3", TipoPedido.DELIVERY, EstadoPedido.ENTREGADO, MetodoPago.TARJETA, 500, 200, Jun(11), "111");
            m3.PedidoDetalles.Add(Detalle("dd3", m3.Id, 1, 500, ProdAId));
            // m4: RETIRO / EFECTIVO / 800 / cliente 333 / 11-jun / Pizza x1 (800)
            var m4 = Pedido("dm4", TipoPedido.RETIRO, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 800, 0, Jun(11), "333");
            m4.PedidoDetalles.Add(Detalle("dd4", m4.Id, 1, 800, ProdAId));
            // m5: CANCELADO (cuenta en pedidosPorEstado, no en finanzas). m6: PENDIENTE (idem).
            var m5 = Pedido("dm5", TipoPedido.LOCAL, EstadoPedido.CANCELADO, MetodoPago.EFECTIVO, 0, 0, Jun(12), null);
            var m6 = Pedido("dm6", TipoPedido.LOCAL, EstadoPedido.PENDIENTE, null, 700, 0, Jun(12), null);
            // prev: ENTREGADO en el período ANTERIOR (mayo) → alimenta ventasSemanaAnterior.
            var prev = Pedido("dm-prev", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 2000, 0, new DateTime(2026, 5, 15, 15, 0, 0), "999");

            db.Pedidos.AddRange(m1, m2, m3, m4, m5, m6, prev);
            db.GastoOperativos.Add(new GastoOperativo
            {
                Id = "dg1", Categoria = "INSUMOS", Monto = 800, Fecha = Jun(10),
                NegocioId = NegocioAId, CreatedAt = Now(), UpdatedAt = Now(),
            });
            await db.SaveChangesAsync();
        });

        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var m = (await client.GetFromJsonAsync<MetricsDto>("/dashboard/metrics?desde=2026-06-01&hasta=2026-06-30"))!;

        // Totales (sólo ENTREGADO de junio): 1000 + 1500 + 500 + 800 = 3800.
        Assert.Equal(3800, m.TotalFacturado);
        Assert.Equal(3800, m.TotalNegocio);
        Assert.Equal(4, m.TotalPedidos);
        Assert.Equal(950, m.Promedio);
        Assert.Equal(500, m.TotalDelivery);        // envíos: 300 (m2) + 200 (m3)

        // Corte por método de pago.
        Assert.Equal(1800, m.Efectivo);            // m1 1000 + m4 800
        Assert.Equal(1500, m.Transferencia);       // m2
        Assert.Equal(500, m.Tarjeta);              // m3

        // Gastos y ganancia neta.
        Assert.Equal(800, m.TotalGastos);
        Assert.Equal(3000, m.GananciaNeta);        // 3800 − 800

        // porTipo: LOCAL {1,1000}, DELIVERY {2,2000}, RETIRO {1,800}.
        Assert.Equal((1, 1000), Tipo(m, TipoPedido.LOCAL));
        Assert.Equal((2, 2000), Tipo(m, TipoPedido.DELIVERY));
        Assert.Equal((1, 800), Tipo(m, TipoPedido.RETIRO));

        // topProductos: Pizza, cantidad 2+1+1+1 = 5, total 2000+1000+500+800 = 4300.
        var pizza = Assert.Single(m.TopProductos);
        Assert.Equal("Pizza", pizza.Nombre);
        Assert.Equal(5, pizza.Cantidad);
        Assert.Equal(4300, pizza.Total);

        // clientes period-scoped: 111 con 2 pedidos (recurrente); 222 y 333 con 1 (nuevos).
        Assert.Equal(2, m.ClientesNuevos);
        Assert.Equal(1, m.ClientesRecurrentes);

        // pedidosPorEstado (todos los estados de junio): ENTREGADO 4, CANCELADO 1, PENDIENTE 1.
        Assert.Equal(4, Estado(m.PedidosPorEstado, EstadoPedido.ENTREGADO));
        Assert.Equal(1, Estado(m.PedidosPorEstado, EstadoPedido.CANCELADO));
        Assert.Equal(1, Estado(m.PedidosPorEstado, EstadoPedido.PENDIENTE));

        // porDia (AR): 10-jun {2, 2500}, 11-jun {2, 1300}.
        Assert.Equal((2, 2500), Dia(m, "2026-06-10"));
        Assert.Equal((2, 1300), Dia(m, "2026-06-11"));

        // porHora: los 4 entregados caen a las 12:00 AR (15:00 UTC).
        var hora = Assert.Single(m.PorHora);
        Assert.Equal(12, hora.Hora);
        Assert.Equal(4, hora.Pedidos);
        Assert.Equal(3800, hora.Total);

        // Comparativa contra el período anterior: prev = 2000 → (3800 − 2000) / 2000 * 100 = 90%.
        Assert.Equal(2000, m.VentasSemanaAnterior);
        Assert.Equal(90, m.Comparativa, 3);
    }

    [Fact]
    public async Task Metrics_valida_fechas_requeridas_orden_y_rango_maximo()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        // Faltan las fechas → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/metrics")).StatusCode);
        // Sólo una → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/metrics?desde=2026-06-01")).StatusCode);
        // desde > hasta → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/metrics?desde=2026-06-30&hasta=2026-06-01")).StatusCode);
        // Rango > 90 días → 400 (01-abr a 30-jun = 91 días inclusive).
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/metrics?desde=2026-04-01&hasta=2026-06-30")).StatusCode);
        // Rango en el borde exacto (02-abr a 30-jun = 90 días inclusive) → 200.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard/metrics?desde=2026-04-02&hasta=2026-06-30")).StatusCode);
    }

    [Fact]
    public async Task Metrics_y_resumen_son_solo_admin()
    {
        var trab = await LoginAsync(TrabajadorAEmail, NegocioASlug);
        Assert.Equal(HttpStatusCode.Forbidden, (await trab.GetAsync("/dashboard/metrics?desde=2026-06-01&hasta=2026-06-30")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trab.GetAsync("/dashboard/resumen-hoy")).StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RESUMEN HOY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResumenHoy_sin_turno_cuenta_activos_facturado_y_cuentas_abiertas()
    {
        await UsingDbAsync(async db =>
        {
            var hoy = Now();
            // Activos (en vivo, sin filtro de fecha): 2 PENDIENTE + 1 EN_PREPARACION. ENTREGADO/CANCELADO no cuentan.
            db.Pedidos.Add(Pedido("rh1", TipoPedido.LOCAL, EstadoPedido.PENDIENTE, null, 500, 0, hoy, null));
            db.Pedidos.Add(Pedido("rh2", TipoPedido.DELIVERY, EstadoPedido.PENDIENTE, null, 700, 0, hoy, null));
            db.Pedidos.Add(Pedido("rh3", TipoPedido.LOCAL, EstadoPedido.EN_PREPARACION, null, 900, 0, hoy, null));
            // Facturado hoy: 2 ENTREGADO de hoy (1000 + 500 = 1500).
            db.Pedidos.Add(Pedido("rh4", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 1000, 0, hoy, null));
            db.Pedidos.Add(Pedido("rh5", TipoPedido.RETIRO, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 500, 0, hoy, null));
            // Cuenta abierta (pendiente de pago, no cancelada): cuenta en el bloque de cuentas abiertas.
            var abierta = Pedido("rh6", TipoPedido.LOCAL, EstadoPedido.PENDIENTE, null, 1200, 0, hoy, null);
            abierta.CuentaAbierta = true;
            abierta.EstadoPago = EstadoPago.PENDIENTE;
            db.Pedidos.Add(abierta);
            await db.SaveChangesAsync();
        });

        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var r = (await client.GetFromJsonAsync<ResumenHoyDto>("/dashboard/resumen-hoy"))!;

        Assert.Null(r.Turno);

        // Activos: rh1, rh2, rh3 y rh6 (PENDIENTE también) = 4 en total; PENDIENTE 3, EN_PREPARACION 1.
        Assert.Equal(4, r.PedidosActivosTotal);
        Assert.Equal(3, Estado(r.PedidosActivos, EstadoPedido.PENDIENTE));
        Assert.Equal(1, Estado(r.PedidosActivos, EstadoPedido.EN_PREPARACION));

        Assert.Equal(1500, r.FacturadoHoy);

        Assert.Equal(1, r.CuentasAbiertasCount);
        Assert.Equal(1200, r.CuentasAbiertasTotal);
    }

    [Fact]
    public async Task ResumenHoy_con_turno_activo_devuelve_ventas_y_esperado()
    {
        await UsingDbAsync(async db =>
        {
            // Un ENTREGADO de hoy para facturadoHoy.
            db.Pedidos.Add(Pedido("rht1", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 2500, 0, Now(), null));
            await db.SaveChangesAsync();
        });

        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        // Abrir turno con fondo inicial 5000.
        var abrir = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 5000 });
        Assert.Equal(HttpStatusCode.OK, abrir.StatusCode);

        // Movimiento dentro del turno: ENTRADA manual de 3000 (efectivo, sin pedido) → ventas 3000, efectivo 3000.
        await UsingDbAsync(async db =>
        {
            db.CajaMovimientos.Add(new CajaMovimiento
            {
                Id = Guid.NewGuid().ToString(),
                NegocioId = NegocioAId,
                Tipo = TipoMovimientoCaja.ENTRADA,
                MontoTotal = 3000,
                GananciaNegocio = 3000,
                GananciaRepartidor = 0,
                Descripcion = "seed",
                FechaConfirmacion = Now(),
                Anulado = false,
                CreatedAt = Now(),
            });
            await db.SaveChangesAsync();
        });

        var r = (await client.GetFromJsonAsync<ResumenHoyDto>("/dashboard/resumen-hoy"))!;

        Assert.NotNull(r.Turno);
        Assert.Equal(3000, r.Turno!.VentasEnVivo);
        Assert.Equal(8000, r.Turno.MontoEsperado);   // 5000 apertura + 3000 efectivo
        Assert.Equal(2500, r.FacturadoHoy);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AISLAMIENTO MULTI-TENANT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Metrics_aislado_por_negocio()
    {
        DateTime Jun(int day) => new(2026, 6, day, 15, 0, 0);

        await UsingDbAsync(async db =>
        {
            // Sólo negocio A tiene ventas en junio.
            var a = Pedido("iso-a", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 1000, 0, Jun(10), "111", NegocioAId);
            a.PedidoDetalles.Add(Detalle("iso-da", a.Id, 1, 1000, ProdAId, NegocioAId));
            db.Pedidos.Add(a);
            await db.SaveChangesAsync();
        });

        // B no ve nada de A: sus métricas son cero.
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);
        var mB = (await clientB.GetFromJsonAsync<MetricsDto>("/dashboard/metrics?desde=2026-06-01&hasta=2026-06-30"))!;
        Assert.Equal(0, mB.TotalFacturado);
        Assert.Equal(0, mB.TotalPedidos);
        Assert.Empty(mB.TopProductos);

        // A sí ve lo suyo.
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var mA = (await clientA.GetFromJsonAsync<MetricsDto>("/dashboard/metrics?desde=2026-06-01&hasta=2026-06-30"))!;
        Assert.Equal(1000, mA.TotalFacturado);
        Assert.Equal(1, mA.TotalPedidos);
    }

    // ── Helpers de seed ─────────────────────────────────────────────────────────

    private static Pedido Pedido(
        string id, TipoPedido tipo, EstadoPedido estado, MetodoPago? metodoPago, double total, double costoEnvio,
        DateTime createdAt, string? numeroCliente, string negocioId = NegocioAId) => new()
        {
            Id = id,
            Tipo = tipo,
            Estado = estado,
            MetodoPago = metodoPago,
            Total = total,
            CostoEnvio = costoEnvio,
            CuentaAbierta = false,
            EstadoPago = EstadoPago.PENDIENTE,
            NumeroCliente = numeroCliente,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Unspecified),
            NegocioId = negocioId,
        };

    private static PedidoDetalle Detalle(string id, string pedidoId, int cantidad, double subtotal, string productoId, string negocioId = NegocioAId) => new()
    {
        Id = id,
        PedidoId = pedidoId,
        ProductoId = productoId,
        Cantidad = cantidad,
        Subtotal = subtotal,
        PrecioUnitario = 1000,
        SinExtras = false,
        ImpresoEnCocina = false,
        NegocioId = negocioId,
    };

    // ── Helpers de aserción ──────────────────────────────────────────────────────

    private static (int Cantidad, double Total) Tipo(MetricsDto m, TipoPedido tipo)
    {
        var t = m.PorTipo.Single(x => x.Tipo == tipo);
        return (t.Cantidad, t.Total);
    }

    private static (int Pedidos, double Total) Dia(MetricsDto m, string fecha)
    {
        var d = m.PorDia.Single(x => x.Fecha == fecha);
        return (d.Pedidos, d.Total);
    }

    private static int Estado(IEnumerable<EstadoConteoDto> lista, EstadoPedido estado) =>
        lista.SingleOrDefault(x => x.Estado == estado)?.Count ?? 0;

    // ── Infra ────────────────────────────────────────────────────────────────────

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

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    // ── DTOs de respuesta ─────────────────────────────────────────────────────────

    private sealed record MetricsDto(
        double TotalFacturado, double TotalNegocio, double TotalDelivery, int TotalPedidos, double Promedio,
        double Efectivo, double Transferencia, double Tarjeta, double TotalGastos, double GananciaNeta,
        List<DiaDto> PorDia, List<HoraDto> PorHora, List<ProductoDto> TopProductos, List<TipoDto> PorTipo,
        int ClientesNuevos, int ClientesRecurrentes, double VentasSemanaAnterior, double Comparativa,
        List<EstadoConteoDto> PedidosPorEstado);

    private sealed record DiaDto(string Fecha, int Pedidos, double Total);
    private sealed record HoraDto(int Hora, int Pedidos, double Total);
    private sealed record ProductoDto(string Nombre, int Cantidad, double Total);
    private sealed record TipoDto(TipoPedido Tipo, int Cantidad, double Total);
    private sealed record EstadoConteoDto(EstadoPedido Estado, int Count);

    private sealed record ResumenHoyDto(
        List<EstadoConteoDto> PedidosActivos, int PedidosActivosTotal, double FacturadoHoy,
        TurnoDto? Turno, int CuentasAbiertasCount, double CuentasAbiertasTotal);

    private sealed record TurnoDto(string Id, DateTime HoraInicio, double VentasEnVivo, double MontoEsperado);
}
