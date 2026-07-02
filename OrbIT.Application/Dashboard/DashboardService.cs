using Microsoft.EntityFrameworkCore;
using OrbIT.Application.Common;
using OrbIT.Application.Turnos;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Dashboard;

/// <summary>
/// Implementación de <see cref="IDashboardService"/>. Todo el aislamiento multi-tenant lo da el Global Query
/// Filter (scopeado por el claim del ADMIN autenticado), igual que el resto de los reads del proyecto: las
/// queries no filtran <c>NegocioId</c> a mano. El turno activo se delega en <see cref="ITurnoService"/>
/// (única lógica reutilizada); el resto son queries propias con GroupBy/Sum en Postgres.
///
/// <para>Las agregaciones de dinero/tipo/estado/clientes salen de GroupBy/Sum server-side. Sólo el desglose por
/// día y por hora se bucketea en memoria sobre una projection mínima (<c>{CreatedAt, Total}</c>): la conversión
/// a hora de pared AR no es expresable en LINQ→SQL de forma portable (misma limitación ya asumida en
/// <c>/pedidos/reporte</c> y <c>/pedidos/stats</c>). La mejora central sobre el NestJS es no traer los pedidos
/// con sus detalles anidados a memoria.</para>
/// </summary>
public sealed class DashboardService : IDashboardService
{
    /// <summary>Tope de rango para <c>metrics</c>: protege la performance de las agregaciones.</summary>
    private const int MaxRangoDias = 90;

    private readonly OrbitDbContext _db;
    private readonly ITurnoService _turnos;

    public DashboardService(OrbitDbContext db, ITurnoService turnos)
    {
        _db = db;
        _turnos = turnos;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // METRICS (período)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<MetricsResult> GetMetricsAsync(string? desde, string? hasta, string negocioId, CancellationToken ct = default)
    {
        if (ArgentinaClock.DesdeArUtc(desde) is not { } desdeUtc || ArgentinaClock.HastaArUtc(hasta) is not { } hastaUtc)
        {
            throw DashboardException.BadRequest("Los parámetros 'desde' y 'hasta' son requeridos y deben ser fechas válidas.");
        }
        if (desdeUtc > hastaUtc)
        {
            throw DashboardException.BadRequest("La fecha 'desde' no puede ser mayor que 'hasta'.");
        }
        if ((hastaUtc - desdeUtc).TotalDays > MaxRangoDias)
        {
            throw DashboardException.BadRequest($"El rango no puede exceder {MaxRangoDias} días.");
        }

        // Base: sólo ENTREGADO del período (para toda la parte financiera, misma semántica que /pedidos/reporte).
        var entregados = _db.Pedidos.AsNoTracking()
            .Where(p => p.Estado == EstadoPedido.ENTREGADO && p.CreatedAt >= desdeUtc && p.CreatedAt <= hastaUtc);

        // (1) Totales + corte por método de pago + delivery, en un solo agregado server-side.
        var tot = await entregados
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalFacturado = g.Sum(p => p.Total),
                TotalDelivery = g.Sum(p => p.CostoEnvio),
                TotalPedidos = g.Count(),
                Efectivo = g.Sum(p => p.MetodoPago == MetodoPago.EFECTIVO ? p.Total : 0d),
                Transferencia = g.Sum(p => p.MetodoPago == MetodoPago.TRANSFERENCIA ? p.Total : 0d),
                Tarjeta = g.Sum(p => p.MetodoPago == MetodoPago.TARJETA ? p.Total : 0d),
            })
            .FirstOrDefaultAsync(ct);

        var totalFacturado = tot?.TotalFacturado ?? 0;
        var totalPedidos = tot?.TotalPedidos ?? 0;

        // (2) porTipo: GroupBy server-side (cantidad + facturación).
        var porTipoRaw = await entregados
            .GroupBy(p => p.Tipo)
            .Select(g => new { Tipo = g.Key, Cantidad = g.Count(), Total = g.Sum(p => p.Total) })
            .ToListAsync(ct);
        var porTipo = porTipoRaw
            .OrderByDescending(x => x.Total)
            .Select(x => new MetricaTipo(x.Tipo, x.Cantidad, x.Total))
            .ToList();

        // (3) porDia + porHora: projection mínima {CreatedAt, Total}; el bucket AR se hace en memoria (ver clase).
        var puntos = await entregados
            .Select(p => new { p.CreatedAt, p.Total })
            .ToListAsync(ct);

        var porDiaMap = new Dictionary<string, (int Pedidos, double Total)>();
        var porHoraMap = new Dictionary<int, (int Pedidos, double Total)>();
        foreach (var pt in puntos)
        {
            var local = ArgentinaClock.ToLocal(pt.CreatedAt);
            var dia = local.ToString("yyyy-MM-dd");
            var accD = porDiaMap.GetValueOrDefault(dia);
            porDiaMap[dia] = (accD.Pedidos + 1, accD.Total + pt.Total);
            var accH = porHoraMap.GetValueOrDefault(local.Hour);
            porHoraMap[local.Hour] = (accH.Pedidos + 1, accH.Total + pt.Total);
        }
        var porDia = porDiaMap.OrderBy(kv => kv.Key)
            .Select(kv => new MetricaDia(kv.Key, kv.Value.Pedidos, kv.Value.Total)).ToList();
        var porHora = porHoraMap.OrderBy(kv => kv.Key)
            .Select(kv => new MetricaHora(kv.Key, kv.Value.Pedidos, kv.Value.Total)).ToList();

        // (4) topProductos: GroupBy server-side sobre los detalles de los pedidos ENTREGADO del período.
        var topProductosRaw = await _db.PedidoDetalles.AsNoTracking()
            .Where(dt => dt.Pedido.Estado == EstadoPedido.ENTREGADO
                && dt.Pedido.CreatedAt >= desdeUtc && dt.Pedido.CreatedAt <= hastaUtc)
            .GroupBy(dt => dt.Producto.Nombre)
            .Select(g => new { Nombre = g.Key, Cantidad = g.Sum(x => x.Cantidad), Total = g.Sum(x => x.Subtotal) })
            .OrderByDescending(x => x.Cantidad)
            .Take(10)
            .ToListAsync(ct);
        var topProductos = topProductosRaw
            .Select(x => new MetricaProducto(x.Nombre ?? "Desconocido", x.Cantidad, x.Total))
            .ToList();

        // (5) pedidosPorEstado: GroupBy server-side sobre TODOS los estados del período (sin filtro ENTREGADO).
        var porEstadoRaw = await _db.Pedidos.AsNoTracking()
            .Where(p => p.CreatedAt >= desdeUtc && p.CreatedAt <= hastaUtc)
            .GroupBy(p => p.Estado)
            .Select(g => new { Estado = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var pedidosPorEstado = porEstadoRaw
            .OrderByDescending(x => x.Count)
            .Select(x => new MetricaEstado(x.Estado, x.Count))
            .ToList();

        // (6) clientesNuevos/recurrentes: period-scoped. Se agrupa por numeroCliente server-side y se cuenta en
        // memoria sobre los clientes distintos (nuevos = 1 pedido en el período, recurrentes = >1).
        var pedidosPorCliente = await entregados
            .Where(p => p.NumeroCliente != null)
            .GroupBy(p => p.NumeroCliente)
            .Select(g => g.Count())
            .ToListAsync(ct);
        var clientesNuevos = pedidosPorCliente.Count(c => c == 1);
        var clientesRecurrentes = pedidosPorCliente.Count(c => c > 1);

        // (7) totalGastos: Sum server-side (filtra por Fecha AR, misma semántica que /gastos/resumen).
        var totalGastos = await _db.GastoOperativos.AsNoTracking()
            .Where(g => g.Fecha >= desdeUtc && g.Fecha <= hastaUtc)
            .SumAsync(g => g.Monto, ct);

        // (8) ventasSemanaAnterior: mismo tramo desplazado hacia atrás (idéntico al NestJS). Comparativa en %.
        var span = hastaUtc - desdeUtc;
        var desdeAnterior = desdeUtc - span;
        var hastaAnterior = hastaUtc - span;
        var ventasSemanaAnterior = await _db.Pedidos.AsNoTracking()
            .Where(p => p.Estado == EstadoPedido.ENTREGADO
                && p.CreatedAt >= desdeAnterior && p.CreatedAt <= hastaAnterior)
            .SumAsync(p => p.Total, ct);
        var comparativa = ventasSemanaAnterior > 0
            ? (totalFacturado - ventasSemanaAnterior) / ventasSemanaAnterior * 100
            : 0;

        return new MetricsResult(
            totalFacturado,
            totalFacturado, // totalNegocio (== facturado, paridad con NestJS)
            tot?.TotalDelivery ?? 0,
            totalPedidos,
            totalPedidos > 0 ? totalFacturado / totalPedidos : 0,
            tot?.Efectivo ?? 0,
            tot?.Transferencia ?? 0,
            tot?.Tarjeta ?? 0,
            totalGastos,
            totalFacturado - totalGastos,
            porDia,
            porHora,
            topProductos,
            porTipo,
            clientesNuevos,
            clientesRecurrentes,
            ventasSemanaAnterior,
            comparativa,
            pedidosPorEstado);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RESUMEN HOY (en vivo)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<ResumenHoyResult> GetResumenHoyAsync(string negocioId, CancellationToken ct = default)
    {
        // (1) Pedidos activos por estado: en curso = todo salvo ENTREGADO/CANCELADO (sin filtro de fecha, es "en vivo").
        var activosRaw = await _db.Pedidos.AsNoTracking()
            .Where(p => p.Estado != EstadoPedido.ENTREGADO && p.Estado != EstadoPedido.CANCELADO)
            .GroupBy(p => p.Estado)
            .Select(g => new { Estado = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var pedidosActivos = activosRaw
            .OrderByDescending(x => x.Count)
            .Select(x => new MetricaEstado(x.Estado, x.Count))
            .ToList();
        var pedidosActivosTotal = activosRaw.Sum(x => x.Count);

        // (2) Facturado hoy: Sum de ENTREGADO del día AR actual.
        var hoy = ArgentinaClock.Now().Date;
        var desdeHoy = ArgentinaClock.ToUtc(hoy);
        var hastaHoy = ArgentinaClock.ToUtc(hoy.AddDays(1).AddTicks(-1));
        var facturadoHoy = await _db.Pedidos.AsNoTracking()
            .Where(p => p.Estado == EstadoPedido.ENTREGADO && p.CreatedAt >= desdeHoy && p.CreatedAt <= hastaHoy)
            .SumAsync(p => p.Total, ct);

        // (3) Turno activo: única lógica reutilizada (ventas en vivo + efectivo esperado).
        var turnoActivo = await _turnos.ObtenerTurnoActivoAsync(negocioId, ct);
        var turno = turnoActivo is null
            ? null
            : new ResumenTurno(turnoActivo.Turno.Id, turnoActivo.Turno.HoraInicio, turnoActivo.VentasEnVivo, turnoActivo.MontoEsperado);

        // (4) Cuentas abiertas: re-query directo (count + total en un agregado, sin traer filas).
        var cuentas = await _db.Pedidos.AsNoTracking()
            .Where(p => p.CuentaAbierta && p.EstadoPago == EstadoPago.PENDIENTE && p.Estado != EstadoPedido.CANCELADO)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(p => p.Total) })
            .FirstOrDefaultAsync(ct);

        return new ResumenHoyResult(
            pedidosActivos,
            pedidosActivosTotal,
            facturadoHoy,
            turno,
            cuentas?.Count ?? 0,
            cuentas?.Total ?? 0);
    }
}
