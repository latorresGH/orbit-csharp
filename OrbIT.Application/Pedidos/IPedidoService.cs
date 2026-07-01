using OrbIT.Domain.Enums;

namespace OrbIT.Application.Pedidos;

// ── Inputs (la Api mapea su request a estos records; Application no depende de los contracts de Api) ──

public sealed record PedidoExtraInput(string ExtraId, int Cantidad);

public sealed record MediaMediaInput(string? Sabor1Id, string? Sabor2Id);

public sealed record PedidoLineaInput(
    string ProductoId,
    int Cantidad,
    string? Notas,
    double? PrecioUnitario,
    IReadOnlyList<PedidoExtraInput>? Extras,
    IReadOnlyList<string>? AderezosIds,
    bool SinExtras,
    bool ImpresoEnCocina,
    MediaMediaInput? MediaMedia);

public sealed record CrearPedidoInput(
    TipoPedido Tipo,
    string? NombreCliente,
    string? ApellidoCliente,
    string? NumeroCliente,
    MetodoPago? MetodoPago,
    string? Direccion,
    double? CostoEnvio,
    double? DireccionLat,
    double? DireccionLng,
    string? DireccionFormateada,
    string? Piso,
    string? Departamento,
    string? Referencias,
    string? NotasRepartidor,
    string? ShippingZoneName,
    string? ShippingReason,
    string? DireccionPrecision,
    string? PedidoId,
    bool CuentaAbierta,
    EstadoPago? EstadoPago,
    string? RepartidorId,
    string? MesaId,
    string? Origen,
    string? CodigoDescuento,
    IReadOnlyList<PedidoLineaInput> Detalles,
    bool EsAutenticado);

/// <summary>Snapshot de un extra que se serializa en la columna jsonb <c>PedidoDetalle.Extras</c>.</summary>
public sealed record ExtraSnapshot(string Id, string Nombre, double Precio, bool Cobrado);

/// <summary>
/// Excepción de dominio del flujo de pedidos. El controller la mapea a la respuesta HTTP
/// correspondiente (<see cref="StatusCode"/> 400/404), replicando los <c>BadRequestException</c> /
/// <c>NotFoundException</c> de NestJS sin acoplar Application a ASP.NET.
/// </summary>
public sealed class PedidoException : Exception
{
    public int StatusCode { get; }

    private PedidoException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public static PedidoException BadRequest(string message) => new(400, message);

    public static PedidoException NotFound(string message) => new(404, message);
}

/// <summary>
/// Orquestador de las dos operaciones transaccionales y pesadas de stock del módulo Pedidos:
/// creación (con pricing, descuento de stock, ofertas/códigos, upsert de cliente y ocupación de mesa) y
/// cancelación (con reversión de stock). El resto de los endpoints viven en el controller.
/// </summary>
public interface IPedidoService
{
    /// <summary>Crea (o agrega ítems a) un pedido. Devuelve el id del pedido creado/actualizado.</summary>
    Task<string> CrearPedidoAsync(CrearPedidoInput input, string negocioId, CancellationToken cancellationToken = default);

    /// <summary>Cancela un pedido y revierte el stock consumido. <paramref name="canceladoPor"/> viene del claim del actor.</summary>
    Task CancelarPedidoAsync(string pedidoId, string motivo, Role canceladoPor, string negocioId, CancellationToken cancellationToken = default);
}
