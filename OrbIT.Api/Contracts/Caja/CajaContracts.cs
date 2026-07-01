using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Caja;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class ConfirmarPagoRequest
{
    public double? GananciaRepartidor { get; set; }

    public MetodoPago? MetodoPago { get; set; }
}

public sealed class MovimientoManualRequest
{
    /// <summary>Solo ENTRADA / SALIDA / AJUSTE. Los *_TURNO los emite el flujo de turnos, no el usuario.</summary>
    [Required]
    public TipoMovimientoCaja Tipo { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "El monto debe ser mayor a 0.")]
    public double Monto { get; set; }

    public string? Descripcion { get; set; }
}

public sealed class AnularMovimientoRequest
{
    public string? Motivo { get; set; }
}

public sealed class ConfirmarTodosRequest
{
    [Required]
    public List<string> PedidoIds { get; set; } = new();
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record CajaMovimientoResponse(
    string Id,
    string? PedidoId,
    TipoMovimientoCaja Tipo,
    double MontoTotal,
    double GananciaNegocio,
    double GananciaRepartidor,
    string? Descripcion,
    string? ConfirmadoPor,
    DateTime? FechaConfirmacion,
    bool Anulado,
    DateTime CreatedAt,
    CajaMovimientoPedidoResponse? Pedido);

/// <summary>Datos mínimos del pedido asociado a un movimiento (para el resumen / detalle por pedido).</summary>
public sealed record CajaMovimientoPedidoResponse(
    string Id,
    string? NombreCliente,
    string? ApellidoCliente,
    double Total,
    EstadoPedido Estado);

public sealed record CajaResumen(
    double TotalEntradas,
    double TotalSalidas,
    double GananciaNegocioTotal,
    double GananciaRepartidorTotal,
    double Balance);

/// <summary>Resumen agregado (server-side) + lista de movimientos paginada.</summary>
public sealed record CajaResumenResponse(
    CajaResumen Resumen,
    IReadOnlyList<CajaMovimientoResponse> Movimientos,
    int Total,
    int Page,
    int TotalPages);

// ── Pendientes de cobro / cuentas abiertas ───────────────────────────────────

public sealed record PendienteCobroResponse(
    string Id,
    string? NombreCliente,
    string? ApellidoCliente,
    string? NumeroCliente,
    double Total,
    double CostoEnvio,
    TipoPedido Tipo,
    EstadoPedido Estado,
    DateTime CreatedAt,
    IReadOnlyList<PendienteCobroDetalle> Detalles);

public sealed record PendienteCobroDetalle(string Id, string? Producto, int Cantidad, double Subtotal);

public sealed record CuentaAbiertaResumenResponse(int Cantidad, double Total, IReadOnlyList<CuentaAbiertaItem> Cuentas);

public sealed record CuentaAbiertaItem(string Id, double Total, string? NombreCliente, DateTime CreatedAt);
