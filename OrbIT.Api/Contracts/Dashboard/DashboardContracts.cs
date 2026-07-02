using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Dashboard;

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>Métricas agregadas del período. Ver <c>IDashboardService.GetMetricsAsync</c>.</summary>
public sealed record MetricsResponse(
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
    IReadOnlyList<MetricaDiaResponse> PorDia,
    IReadOnlyList<MetricaHoraResponse> PorHora,
    IReadOnlyList<MetricaProductoResponse> TopProductos,
    IReadOnlyList<MetricaTipoResponse> PorTipo,
    int ClientesNuevos,
    int ClientesRecurrentes,
    double VentasSemanaAnterior,
    double Comparativa,
    IReadOnlyList<MetricaEstadoResponse> PedidosPorEstado);

public sealed record MetricaDiaResponse(string Fecha, int Pedidos, double Total);

public sealed record MetricaHoraResponse(int Hora, int Pedidos, double Total);

public sealed record MetricaProductoResponse(string Nombre, int Cantidad, double Total);

public sealed record MetricaTipoResponse(TipoPedido Tipo, int Cantidad, double Total);

public sealed record MetricaEstadoResponse(EstadoPedido Estado, int Count);

/// <summary>Datos en vivo del día. Ver <c>IDashboardService.GetResumenHoyAsync</c>.</summary>
public sealed record ResumenHoyResponse(
    IReadOnlyList<MetricaEstadoResponse> PedidosActivos,
    int PedidosActivosTotal,
    double FacturadoHoy,
    ResumenTurnoResponse? Turno,
    int CuentasAbiertasCount,
    double CuentasAbiertasTotal);

public sealed record ResumenTurnoResponse(string Id, DateTime HoraInicio, double VentasEnVivo, double MontoEsperado);
