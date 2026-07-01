using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Pedidos;

/// <summary>
/// Tanda B (reporting/stats/historial): endpoints de solo lectura. Se siembran pedidos con estado, tipo,
/// método de pago, extras (jsonb) y aderezos (M:N) conocidos, y se verifica la forma y las métricas de
/// <c>/historial</c>, <c>/stats</c>, <c>/stats/cocina</c> y <c>/reporte</c> por HTTP real.
/// </summary>
[Collection(PedidoApiCollection.Name)]
public sealed class PedidosReportingTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";
    private const string NegocioId = "neg-rep";
    private const string NegocioSlug = "negocio-rep";
    private const string AdminEmail = "admin-rep@pe.test";
    private const string RepartidorId = "user-rep-delivery";
    private const string ProdId = "prod-rep";
    private const string AdeId = "ade-rep";

    private readonly PedidoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        // Mediodía UTC de una fecha fija: seguro dentro del mismo día AR sin importar el offset -03:00.
        var baseTs = DateTime.SpecifyKind(new DateTime(2026, 6, 15, 15, 0, 0), DateTimeKind.Unspecified);

        db.Negocios.Add(new Negocio { Id = NegocioId, Nombre = "Negocio Rep", Slug = NegocioSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = "user-rep-admin", Email = AdminEmail, Password = hasher.Hash(Password), Nombre = "Admin", Role = Role.ADMIN, Activo = true, NegocioId = NegocioId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = RepartidorId, Email = "delivery-rep@pe.test", Password = hasher.Hash(Password), Nombre = "Pedro Repartidor", Role = Role.DELIVERY, Activo = true, NegocioId = NegocioId, EmailVerificado = true, CreatedAt = now });
        db.Productos.Add(new Producto { Id = ProdId, Nombre = "Pizza", Precio = 1000, Activo = true, NegocioId = NegocioId });
        var aderezo = new Aderezo { Id = AdeId, Nombre = "Ketchup", UnidadMedida = UnidadMedida.MILILITRO, Activo = true, StockActual = 30, EsPremium = true, Precio = 150, NegocioId = NegocioId };
        db.Aderezos.Add(aderezo);

        // p1: ENTREGADO / LOCAL / EFECTIVO / 1000. Detalle Pizza x2 (subtotal 2000), extra Cheddar, aderezo Ketchup.
        var p1 = Pedido("p1-entregado-local", TipoPedido.LOCAL, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 1000, baseTs, nombre: "Juan");
        var d1 = Detalle("d1", p1.Id, 2, 2000, ExtrasJson(("e-cheddar", "Cheddar")));
        d1.As.Add(aderezo);
        p1.PedidoDetalles.Add(d1);

        // p2: ENTREGADO / DELIVERY / TRANSFERENCIA / 1500 con repartidor. Detalle Pizza x1 (1000), extras Cheddar + Bacon.
        var p2 = Pedido("p2-entregado-delivery", TipoPedido.DELIVERY, EstadoPedido.ENTREGADO, MetodoPago.TRANSFERENCIA, 1500, baseTs.AddMinutes(1), repartidorId: RepartidorId, nombre: "Ana");
        p2.PedidoDetalles.Add(Detalle("d2", p2.Id, 1, 1000, ExtrasJson(("e-cheddar", "Cheddar"), ("e-bacon", "Bacon"))));

        // p3: ENTREGADO / DELIVERY / EFECTIVO / 500 con repartidor. Detalle Pizza x1 (500), sin extras.
        var p3 = Pedido("p3-entregado-delivery", TipoPedido.DELIVERY, EstadoPedido.ENTREGADO, MetodoPago.EFECTIVO, 500, baseTs.AddMinutes(2), repartidorId: RepartidorId, nombre: "Ana");
        p3.PedidoDetalles.Add(Detalle("d3", p3.Id, 1, 500, null));

        // p4: CANCELADO / LOCAL (excluido del reporte, cuenta en stats).
        var p4 = Pedido("p4-cancelado", TipoPedido.LOCAL, EstadoPedido.CANCELADO, MetodoPago.EFECTIVO, 0, baseTs.AddMinutes(3), motivo: "Cliente se arrepintió", nombre: "Leo");

        // p5: PENDIENTE / LOCAL (excluido del reporte, cuenta en stats).
        var p5 = Pedido("p5-pendiente", TipoPedido.LOCAL, EstadoPedido.PENDIENTE, null, 800, baseTs.AddMinutes(4), nombre: "Mia");

        db.Pedidos.AddRange(p1, p2, p3, p4, p5);
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
    public async Task Historial_pagina_y_devuelve_forma_data_total_page_totalPages()
    {
        var client = await LoginAsync();

        var r = (await client.GetFromJsonAsync<HistorialResult>("/pedidos/historial?page=1&limit=2"))!;

        Assert.Equal(5, r.Total);          // 5 pedidos sembrados
        Assert.Equal(1, r.Page);
        Assert.Equal(3, r.TotalPages);     // ceil(5 / 2)
        Assert.Equal(2, r.Data.Count);     // primera página, limit 2

        // Orden por createdAt desc: p5 (más nuevo) primero, p4 segundo.
        Assert.Equal("p5-pendiente", r.Data[0].Id);
        Assert.Equal("p4-cancelado", r.Data[1].Id);

        // La última página trae el resto.
        var last = (await client.GetFromJsonAsync<HistorialResult>("/pedidos/historial?page=3&limit=2"))!;
        Assert.Single(last.Data);
        Assert.Equal("p1-entregado-local", last.Data[0].Id);
    }

    [Fact]
    public async Task Stats_metricas_del_periodo()
    {
        var client = await LoginAsync();

        var s = (await client.GetFromJsonAsync<StatsResult>("/pedidos/stats"))!;

        // porTipo: LOCAL = p1,p4,p5 (3); DELIVERY = p2,p3 (2).
        Assert.Equal(3, s.PorTipo.Single(t => t.Tipo == TipoPedido.LOCAL).Count);
        Assert.Equal(2, s.PorTipo.Single(t => t.Tipo == TipoPedido.DELIVERY).Count);

        // cancelaciones: un solo motivo con count 1.
        var motivo = Assert.Single(s.Cancelaciones);
        Assert.Equal("Cliente se arrepintió", motivo.Motivo);
        Assert.Equal(1, motivo.Count);

        // repartidores: Pedro con 2 entregas (p2, p3).
        var rep = Assert.Single(s.Repartidores);
        Assert.Equal("Pedro Repartidor", rep.Nombre);
        Assert.Equal(2, rep.Count);

        // porHora: todos los pedidos caen en alguna hora; el total debe sumar 5.
        Assert.Equal(5, s.PorHora.Sum(h => h.Count));
    }

    [Fact]
    public async Task Reporte_solo_entregados_con_totales_correctos()
    {
        var client = await LoginAsync();

        var rep = (await client.GetFromJsonAsync<ReporteResult>("/pedidos/reporte"))!;

        // Sólo ENTREGADO: p1 (1000) + p2 (1500) + p3 (500). p4 (cancelado) y p5 (pendiente) NO cuentan.
        Assert.Equal(3, rep.TotalPedidos);
        Assert.Equal(3000, rep.TotalFacturado);
        Assert.Equal(1000, rep.Promedio);
        Assert.Equal(1500, rep.Efectivo);       // p1 + p3
        Assert.Equal(1500, rep.Transferencia);  // p2

        // topProductos: Pizza, cantidad 2+1+1 = 4, total 2000+1000+500 = 3500.
        var pizza = Assert.Single(rep.TopProductos);
        Assert.Equal("Pizza", pizza.Nombre);
        Assert.Equal(4, pizza.Cantidad);
        Assert.Equal(3500, pizza.Total);

        // porDia: un solo día con los 3 entregados.
        var dia = Assert.Single(rep.PorDia);
        Assert.Equal(3, dia.Pedidos);
        Assert.Equal(3000, dia.Total);
    }

    [Fact]
    public async Task StatsCocina_agrega_extras_jsonb_y_aderezos()
    {
        var client = await LoginAsync();

        var s = (await client.GetFromJsonAsync<StatsCocinaResult>("/pedidos/stats/cocina"))!;

        // extras (jsonb): Cheddar aparece en p1 y p2 (2), Bacon sólo en p2 (1).
        Assert.Equal(2, s.ExtrasTop.Single(e => e.Nombre == "Cheddar").Cantidad);
        Assert.Equal(1, s.ExtrasTop.Single(e => e.Nombre == "Bacon").Cantidad);

        // aderezos (M:N): Ketchup en p1 (1).
        var ade = Assert.Single(s.AderezosTop);
        Assert.Equal("Ketchup", ade.Nombre);
        Assert.Equal(1, ade.Cantidad);
    }

    // ── Helpers de seed ───────────────────────────────────────────────────────────────────────

    private static Pedido Pedido(
        string id, TipoPedido tipo, EstadoPedido estado, MetodoPago? metodoPago, double total, DateTime createdAt,
        string? repartidorId = null, string? motivo = null, string? nombre = null) => new()
        {
            Id = id,
            Tipo = tipo,
            Estado = estado,
            MetodoPago = metodoPago,
            Total = total,
            CostoEnvio = 0,
            CuentaAbierta = false,
            CreatedAt = createdAt,
            RepartidorId = repartidorId,
            MotivoCancelacion = motivo,
            NombreCliente = nombre,
            NegocioId = NegocioId,
        };

    private static PedidoDetalle Detalle(string id, string pedidoId, int cantidad, double subtotal, string? extrasJson) => new()
    {
        Id = id,
        PedidoId = pedidoId,
        ProductoId = ProdId,
        Cantidad = cantidad,
        Subtotal = subtotal,
        PrecioUnitario = 1000,
        SinExtras = false,
        ImpresoEnCocina = false,
        Extras = extrasJson,
        NegocioId = NegocioId,
    };

    // Espeja el snapshot que serializa PedidoService: array de { Id, Nombre, Precio, Cobrado } (PascalCase).
    private static string ExtrasJson(params (string Id, string Nombre)[] extras) =>
        JsonSerializer.Serialize(extras.Select(e => new { e.Id, e.Nombre, Precio = 200.0, Cobrado = true }));

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email = AdminEmail, password = Password, negocioSlug = NegocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    // ── DTOs de respuesta ─────────────────────────────────────────────────────────────────────

    private sealed record HistorialResult(List<HistorialItem> Data, int Total, int Page, int TotalPages);
    private sealed record HistorialItem(string Id, EstadoPedido Estado, double Total);

    private sealed record StatsResult(List<HoraItem> PorHora, List<TipoItem> PorTipo, List<MotivoItem> Cancelaciones, List<RepItem> Repartidores);
    private sealed record HoraItem(int Hora, int Count);
    private sealed record TipoItem(TipoPedido Tipo, int Count);
    private sealed record MotivoItem(string Motivo, int Count);
    private sealed record RepItem(string Nombre, int Count);

    private sealed record ReporteResult(
        double TotalFacturado, int TotalPedidos, double Promedio, double Efectivo, double Transferencia,
        List<DiaItem> PorDia, List<ProductoItem> TopProductos);
    private sealed record DiaItem(string Fecha, int Pedidos, double Total);
    private sealed record ProductoItem(string Nombre, int Cantidad, double Total);

    private sealed record StatsCocinaResult(List<CocinaTestItem> ExtrasTop, List<CocinaTestItem> AderezosTop);
    private sealed record CocinaTestItem(string Nombre, int Cantidad);
}
