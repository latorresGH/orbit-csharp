using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Pedidos;

// ── Create ───────────────────────────────────────────────────────────────────

public sealed class PedidoExtraRequest
{
    [Required(AllowEmptyStrings = false)]
    public string ExtraId { get; set; } = null!;

    public int? Cantidad { get; set; }
}

public sealed class MediaMediaRequest
{
    public string? Sabor1Id { get; set; }
    public string? Sabor2Id { get; set; }
}

public sealed class PedidoDetalleRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El productoId es obligatorio.")]
    public string ProductoId { get; set; } = null!;

    [Range(1, int.MaxValue, ErrorMessage = "La cantidad debe ser mayor o igual a 1.")]
    public int Cantidad { get; set; }

    public string? Notas { get; set; }

    public double? PrecioUnitario { get; set; }

    public List<PedidoExtraRequest>? Extras { get; set; }

    public List<string>? AderezosIds { get; set; }

    public bool? SinExtras { get; set; }

    public bool? ImpresoEnCocina { get; set; }

    public MediaMediaRequest? MediaMedia { get; set; }
}

public sealed class CreatePedidoRequest
{
    [Required(ErrorMessage = "El tipo es obligatorio.")]
    public TipoPedido Tipo { get; set; }

    public string? NombreCliente { get; set; }
    public string? ApellidoCliente { get; set; }
    public string? NumeroCliente { get; set; }
    public MetodoPago? MetodoPago { get; set; }
    public string? Direccion { get; set; }

    [Range(0, double.MaxValue)]
    public double? CostoEnvio { get; set; }

    public double? DireccionLat { get; set; }
    public double? DireccionLng { get; set; }
    public string? DireccionFormateada { get; set; }
    public string? Piso { get; set; }
    public string? Departamento { get; set; }
    public string? Referencias { get; set; }
    public string? NotasRepartidor { get; set; }
    public string? ShippingZoneName { get; set; }
    public string? ShippingReason { get; set; }
    public string? DireccionPrecision { get; set; }

    /// <summary>Si viene, se agregan ítems a una cuenta abierta existente (en vez de crear un pedido nuevo).</summary>
    public string? PedidoId { get; set; }

    public bool? CuentaAbierta { get; set; }
    public EstadoPago? EstadoPago { get; set; }
    public string? RepartidorId { get; set; }
    public string? MesaId { get; set; }

    /// <summary>"MENU" = pedido del storefront → dispara la notificación de SignalR.</summary>
    public string? Origen { get; set; }

    public string? CodigoDescuento { get; set; }

    [Required, MinLength(1, ErrorMessage = "El pedido no tiene productos.")]
    public List<PedidoDetalleRequest> Detalles { get; set; } = new();
}

// ── Mutaciones operativas ────────────────────────────────────────────────────

public sealed class CambiarEstadoRequest
{
    [Required(ErrorMessage = "El estado es obligatorio.")]
    public EstadoPedido Estado { get; set; }
}

/// <summary>El <c>rol</c> NO viaja en el body: se toma del claim del actor (más seguro que NestJS).</summary>
public sealed class CancelarPedidoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El motivo es obligatorio.")]
    public string Motivo { get; set; } = null!;
}

public sealed class SetPagoRequest
{
    public MetodoPago? MetodoPago { get; set; }
    public string? NumeroCliente { get; set; }
}

public sealed class SetCostoEnvioRequest
{
    [Range(0, double.MaxValue, ErrorMessage = "El costo de envío debe ser mayor o igual a 0.")]
    public double CostoEnvio { get; set; }
}

public sealed class AsignarRepartidorRequest
{
    public string? RepartidorId { get; set; }

    [Range(0, double.MaxValue)]
    public double? CostoEnvio { get; set; }
}

public sealed class AnularCuentaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El motivo es obligatorio.")]
    public string Motivo { get; set; } = null!;
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record ExtraSnapshotResponse(string Id, string Nombre, double Precio, bool Cobrado);

public sealed record AderezoResponse(string Id, string Nombre);

public sealed record MediaMediaResponse(string Sabor1Id, string Sabor2Id);

public sealed record PedidoDetalleResponse(
    string Id,
    string ProductoId,
    string? NombreProducto,
    int Cantidad,
    double PrecioUnitario,
    double Subtotal,
    string? Notas,
    bool SinExtras,
    bool ImpresoEnCocina,
    IReadOnlyList<ExtraSnapshotResponse> Extras,
    IReadOnlyList<AderezoResponse> Aderezos,
    MediaMediaResponse? MediaMedia);

public sealed record PedidoResponse(
    string Id,
    TipoPedido Tipo,
    EstadoPedido Estado,
    EstadoPago EstadoPago,
    MetodoPago? MetodoPago,
    double Total,
    double CostoEnvio,
    string? Direccion,
    string? NombreCliente,
    string? ApellidoCliente,
    string? NumeroCliente,
    string? ClienteId,
    string? MesaId,
    string? RepartidorId,
    bool CuentaAbierta,
    int? DemoraEstimadaMin,
    string? MotivoCancelacion,
    Role? CanceladoPor,
    DateTime CreatedAt,
    IReadOnlyList<PedidoDetalleResponse> Detalles);

public sealed record TrackingDetalleResponse(int Cantidad, string? NombreProducto);

public sealed record TrackingResponse(
    string Id,
    EstadoPedido Estado,
    TipoPedido Tipo,
    int? DemoraEstimadaMin,
    double Total,
    double CostoEnvio,
    bool RepartidorAsignado,
    string? NombreCliente,
    string? Direccion,
    IReadOnlyList<TrackingDetalleResponse> Detalles);

public sealed record ImprimirResponse(int Marcados);

// ── Tanda B: reporting / stats / historial (solo lectura) ──────────────────────

/// <summary>Sobre común de paginación server-side. Espeja el <c>{ data, total, page, totalPages }</c> del NestJS.</summary>
public sealed record PaginatedResponse<T>(IReadOnlyList<T> Data, int Total, int Page, int TotalPages);

/// <summary>
/// Fila del historial: projection liviana (no la entidad completa con triple include del NestJS). Los datos
/// pesados (aderezos, media-media, movimientos de caja) se piden en <c>GET /pedidos/{id}</c> al abrir el detalle.
/// </summary>
public sealed record HistorialPedidoResponse(
    string Id,
    TipoPedido Tipo,
    EstadoPedido Estado,
    EstadoPago EstadoPago,
    MetodoPago? MetodoPago,
    double Total,
    double CostoEnvio,
    string? NombreCliente,
    string? ApellidoCliente,
    string? NumeroCliente,
    string? Direccion,
    string? RepartidorId,
    string? RepartidorNombre,
    bool CuentaAbierta,
    DateTime CreatedAt,
    IReadOnlyList<HistorialDetalleResponse> Detalles);

public sealed record HistorialDetalleResponse(
    string Id,
    string? NombreProducto,
    int Cantidad,
    double PrecioUnitario,
    double Subtotal);

// stats
public sealed record StatsResponse(
    IReadOnlyList<StatHora> PorHora,
    IReadOnlyList<StatTipo> PorTipo,
    IReadOnlyList<StatMotivo> Cancelaciones,
    IReadOnlyList<StatRepartidor> Repartidores);

public sealed record StatHora(int Hora, int Count);
public sealed record StatTipo(TipoPedido Tipo, int Count);
public sealed record StatMotivo(string Motivo, int Count);
public sealed record StatRepartidor(string Nombre, int Count);

// stats/cocina
public sealed record StatsCocinaResponse(
    IReadOnlyList<CocinaItem> ExtrasTop,
    IReadOnlyList<CocinaItem> AderezosTop);

public sealed record CocinaItem(string Nombre, int Cantidad);

// reporte
public sealed record ReporteResponse(
    double TotalFacturado,
    int TotalPedidos,
    double Promedio,
    double Efectivo,
    double Transferencia,
    IReadOnlyList<ReporteDia> PorDia,
    IReadOnlyList<ReporteProducto> TopProductos);

public sealed record ReporteDia(string Fecha, int Pedidos, double Total);
public sealed record ReporteProducto(string Nombre, int Cantidad, double Total);
