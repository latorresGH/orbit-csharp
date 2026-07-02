using OrbIT.Domain.Enums;

namespace OrbIT.Application.Dashboard;

// ── Resultados (la Api mapea estos records a sus responses; Application no depende de los contracts de Api) ──

/// <summary>Métricas agregadas del período (solo pedidos <see cref="EstadoPedido.ENTREGADO"/> salvo donde se indique).</summary>
public sealed record MetricsResult(
    double TotalFacturado,
    double TotalNegocio,
    double TotalDelivery,
    int TotalPedidos,
    double Promedio,
    double Efectivo,
    double Transferencia,
    double Tarjeta,
    double TotalGastos,
    double GananciaNeta,
    IReadOnlyList<MetricaDia> PorDia,
    IReadOnlyList<MetricaHora> PorHora,
    IReadOnlyList<MetricaProducto> TopProductos,
    IReadOnlyList<MetricaTipo> PorTipo,
    int ClientesNuevos,
    int ClientesRecurrentes,
    double VentasSemanaAnterior,
    double Comparativa,
    IReadOnlyList<MetricaEstado> PedidosPorEstado);

/// <summary>Pedidos + facturación de un día AR (<c>yyyy-MM-dd</c>).</summary>
public sealed record MetricaDia(string Fecha, int Pedidos, double Total);

/// <summary>Pedidos + facturación de una hora del día (0..23, hora AR).</summary>
public sealed record MetricaHora(int Hora, int Pedidos, double Total);

/// <summary>Producto más vendido: cantidad de unidades y facturación acumulada.</summary>
public sealed record MetricaProducto(string Nombre, int Cantidad, double Total);

/// <summary>Corte por tipo de pedido (LOCAL/DELIVERY/RETIRO): cantidad y facturación.</summary>
public sealed record MetricaTipo(TipoPedido Tipo, int Cantidad, double Total);

/// <summary>Conteo de pedidos por estado en el período (todos los estados, sin filtrar por ENTREGADO).</summary>
public sealed record MetricaEstado(EstadoPedido Estado, int Count);

/// <summary>Datos en vivo del día (sin período): estado operativo actual del negocio.</summary>
public sealed record ResumenHoyResult(
    IReadOnlyList<MetricaEstado> PedidosActivos,
    int PedidosActivosTotal,
    double FacturadoHoy,
    ResumenTurno? Turno,
    int CuentasAbiertasCount,
    double CuentasAbiertasTotal);

/// <summary>Turno activo con métricas en vivo, o <c>null</c> si no hay turno abierto.</summary>
public sealed record ResumenTurno(string Id, DateTime HoraInicio, double VentasEnVivo, double MontoEsperado);

/// <summary>
/// Excepción de dominio del dashboard. El controller la mapea a la respuesta HTTP correspondiente (400),
/// replicando los <c>BadRequestException</c> de NestJS sin acoplar Application a ASP.NET. Mismo patrón que
/// <c>PedidoException</c> / <c>TurnoException</c>.
/// </summary>
public sealed class DashboardException : Exception
{
    public int StatusCode { get; }

    private DashboardException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public static DashboardException BadRequest(string message) => new(400, message);
}

/// <summary>
/// Agregador de métricas del negocio. Reemplaza el <c>getMetrics</c> monolítico de NestJS (que traía todos los
/// pedidos con sus detalles y agregaba todo en memoria con un loop JS) por queries con GroupBy/Sum server-side
/// en Postgres y projections mínimas. No es un agregador de otros endpoints: consulta la DB directamente.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Métricas del período <c>[desde, hasta]</c> (fechas AR, ambas requeridas). Rango máximo 90 días.
    /// Lanza <see cref="DashboardException"/> (400) si faltan/ no parsean las fechas, si <c>desde &gt; hasta</c>
    /// o si el rango excede 90 días.
    /// </summary>
    Task<MetricsResult> GetMetricsAsync(string? desde, string? hasta, string negocioId, CancellationToken cancellationToken = default);

    /// <summary>Datos en vivo del día: pedidos activos por estado, facturado de hoy (AR), turno activo y cuentas abiertas.</summary>
    Task<ResumenHoyResult> GetResumenHoyAsync(string negocioId, CancellationToken cancellationToken = default);
}
