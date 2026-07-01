using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Caja;

/// <summary>
/// Tests de integración del módulo Caja: registro de pago de pedido (transaccional, estadoPago→PAGADO, guard
/// de doble cobro), movimientos manuales + anulación, resumen agregado, pendientes de cobro y aislamiento
/// multi-tenant.
/// </summary>
[Collection(CajaApiCollection.Name)]
public sealed class CajaIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-ca-a";
    private const string NegocioBId = "neg-ca-b";
    private const string NegocioASlug = "negocio-ca-a";
    private const string NegocioBSlug = "negocio-ca-b";
    private const string AdminAEmail = "admin-a@ca.test";
    private const string TrabajadorAEmail = "trab-a@ca.test";
    private const string AdminBEmail = "admin-b@ca.test";

    private readonly CajaWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio CA A", Slug = NegocioASlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Negocios.Add(new Negocio { Id = NegocioBId, Nombre = "Negocio CA B", Slug = NegocioBSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = "user-ca-a", Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-ca-a-trab", Email = TrabajadorAEmail, Password = hasher.Hash(Password), Nombre = "Trabajador A", Role = Role.TRABAJADOR, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-ca-b", Email = AdminBEmail, Password = hasher.Hash(Password), Nombre = "Admin B", Role = Role.ADMIN, Activo = true, NegocioId = NegocioBId, EmailVerificado = true, CreatedAt = now });

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
    public async Task Confirmar_pago_pedido_es_transaccional_y_marca_pagado()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var pedidoId = await SeedPedidoAsync(NegocioAId, total: 1000, costoEnvio: 200);

        var resp = await client.PostAsJsonAsync($"/caja/pedido/{pedidoId}/confirmar", new { metodoPago = MetodoPago.EFECTIVO });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var mov = (await resp.Content.ReadFromJsonAsync<MovimientoDto>())!;
        Assert.Equal(TipoMovimientoCaja.ENTRADA, mov.Tipo);
        Assert.Equal(1200, mov.MontoTotal);          // 1000 productos + 200 envío
        Assert.Equal(1000, mov.GananciaNegocio);
        Assert.Equal(200, mov.GananciaRepartidor);    // por defecto = costo de envío

        await UsingDbAsync(async db =>
        {
            var pedido = await db.Pedidos.IgnoreQueryFilters().FirstAsync(p => p.Id == pedidoId);
            Assert.Equal(EstadoPago.PAGADO, pedido.EstadoPago);
            Assert.False(pedido.CuentaAbierta);
            Assert.Equal(MetodoPago.EFECTIVO, pedido.MetodoPago);
        });

        // Doble cobro rechazado.
        var otra = await client.PostAsJsonAsync($"/caja/pedido/{pedidoId}/confirmar", new { });
        Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);
    }

    [Fact]
    public async Task Confirmar_pago_pedido_cancelado_devuelve_400()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var pedidoId = await SeedPedidoAsync(NegocioAId, total: 500, costoEnvio: 0, estado: EstadoPedido.CANCELADO);

        var resp = await client.PostAsJsonAsync($"/caja/pedido/{pedidoId}/confirmar", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Movimiento_manual_y_anulacion()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var entrada = await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.ENTRADA, monto = 500, descripcion = "Ingreso extra" });
        Assert.Equal(HttpStatusCode.Created, entrada.StatusCode);
        var mov = (await entrada.Content.ReadFromJsonAsync<MovimientoDto>())!;

        // Tipo *_TURNO no se permite en el endpoint manual.
        var invalido = await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.APERTURA_TURNO, monto = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        // Monto ≤ 0 rechazado por validación.
        var montoCero = await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.ENTRADA, monto = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, montoCero.StatusCode);

        // Anular el movimiento.
        var anular = await client.PostAsJsonAsync($"/caja/movimiento/{mov.Id}/anular", new { motivo = "Cargado por error" });
        Assert.Equal(HttpStatusCode.OK, anular.StatusCode);
        var anulado = (await anular.Content.ReadFromJsonAsync<MovimientoDto>())!;
        Assert.True(anulado.Anulado);

        // Re-anular → 400.
        var reAnular = await client.PostAsJsonAsync($"/caja/movimiento/{mov.Id}/anular", new { motivo = "otra vez" });
        Assert.Equal(HttpStatusCode.BadRequest, reAnular.StatusCode);
    }

    [Fact]
    public async Task Resumen_agrega_server_side_y_excluye_anulados()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.ENTRADA, monto = 500 });
        await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.SALIDA, monto = 200 });
        var extra = await client.PostAsJsonAsync("/caja/movimiento", new { tipo = TipoMovimientoCaja.ENTRADA, monto = 999 });
        var extraMov = (await extra.Content.ReadFromJsonAsync<MovimientoDto>())!;
        await client.PostAsJsonAsync($"/caja/movimiento/{extraMov.Id}/anular", new { motivo = "test" });

        var resumen = (await client.GetFromJsonAsync<ResumenResponse>("/caja/resumen"))!;
        Assert.Equal(500, resumen.Resumen.TotalEntradas);   // el 999 anulado no cuenta
        Assert.Equal(200, resumen.Resumen.TotalSalidas);
        Assert.Equal(300, resumen.Resumen.Balance);
        Assert.Equal(2, resumen.Total);                     // 2 movimientos no anulados
        Assert.Equal(2, resumen.Movimientos.Count);
    }

    [Fact]
    public async Task Pendientes_de_cobro_excluye_los_ya_cobrados()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var sinCobrar = await SeedPedidoAsync(NegocioAId, total: 700, costoEnvio: 0);
        var cobrado = await SeedPedidoAsync(NegocioAId, total: 300, costoEnvio: 0);

        await client.PostAsJsonAsync($"/caja/pedido/{cobrado}/confirmar", new { });

        var pendientes = (await client.GetFromJsonAsync<List<PendienteDto>>("/caja/pendientes-cobro"))!;
        Assert.Contains(pendientes, p => p.Id == sinCobrar);
        Assert.DoesNotContain(pendientes, p => p.Id == cobrado);
    }

    [Fact]
    public async Task Cuentas_abiertas_resumen()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        await SeedPedidoAsync(NegocioAId, total: 1500, costoEnvio: 0, cuentaAbierta: true);
        await SeedPedidoAsync(NegocioAId, total: 2500, costoEnvio: 0, cuentaAbierta: true);

        var resumen = (await client.GetFromJsonAsync<CuentasResumen>("/caja/cuentas-abiertas-resumen"))!;
        Assert.Equal(2, resumen.Cantidad);
        Assert.Equal(4000, resumen.Total);
    }

    [Fact]
    public async Task Confirmar_pago_es_solo_admin()
    {
        var pedidoId = await SeedPedidoAsync(NegocioAId, total: 100, costoEnvio: 0);
        var trab = await LoginAsync(TrabajadorAEmail, NegocioASlug);

        var resp = await trab.PostAsJsonAsync($"/caja/pedido/{pedidoId}/confirmar", new { });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Pero el TRABAJADOR sí puede leer el resumen.
        var resumen = await trab.GetAsync("/caja/resumen");
        Assert.Equal(HttpStatusCode.OK, resumen.StatusCode);
    }

    [Fact]
    public async Task Aislamiento_multitenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var pedidoA = await SeedPedidoAsync(NegocioAId, total: 1000, costoEnvio: 0);

        // B no puede cobrar un pedido de A (no lo ve → 404).
        var cross = await clientB.PostAsJsonAsync($"/caja/pedido/{pedidoA}/confirmar", new { });
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // A cobra y genera un movimiento; el resumen de B no lo ve.
        await clientA.PostAsJsonAsync($"/caja/pedido/{pedidoA}/confirmar", new { });
        var resumenB = (await clientB.GetFromJsonAsync<ResumenResponse>("/caja/resumen"))!;
        Assert.Equal(0, resumenB.Total);
        Assert.Equal(0, resumenB.Resumen.TotalEntradas);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> SeedPedidoAsync(
        string negocioId, double total, double costoEnvio,
        EstadoPedido estado = EstadoPedido.PENDIENTE, bool cuentaAbierta = false)
    {
        var id = Guid.NewGuid().ToString();
        await UsingDbAsync(async db =>
        {
            db.Pedidos.Add(new Pedido
            {
                Id = id,
                NegocioId = negocioId,
                Tipo = TipoPedido.LOCAL,
                Estado = estado,
                EstadoPago = EstadoPago.PENDIENTE,
                Total = total,
                CostoEnvio = costoEnvio,
                CuentaAbierta = cuentaAbierta,
                NombreCliente = "Cliente",
                ApellidoCliente = "Test",
                CreatedAt = Now(),
            });
            await db.SaveChangesAsync();
        });
        return id;
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

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private sealed record MovimientoDto(
        string Id, string? PedidoId, TipoMovimientoCaja Tipo, double MontoTotal, double GananciaNegocio,
        double GananciaRepartidor, string? Descripcion, string? ConfirmadoPor, DateTime? FechaConfirmacion,
        bool Anulado, DateTime CreatedAt);

    private sealed record ResumenDto(
        double TotalEntradas, double TotalSalidas, double GananciaNegocioTotal, double GananciaRepartidorTotal, double Balance);

    private sealed record ResumenResponse(ResumenDto Resumen, List<MovimientoDto> Movimientos, int Total, int Page, int TotalPages);

    private sealed record PendienteDto(string Id, string? NombreCliente, double Total, TipoPedido Tipo, EstadoPedido Estado);

    private sealed record CuentasResumen(int Cantidad, double Total);
}
