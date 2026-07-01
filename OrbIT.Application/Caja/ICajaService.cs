using OrbIT.Domain.Enums;

namespace OrbIT.Application.Caja;

/// <summary>Snapshot del movimiento de caja creado, para devolver al cliente.</summary>
public sealed record CajaMovimientoDto(
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
    DateTime CreatedAt);

/// <summary>Resultado por-pedido del batch de confirmación (mantiene la semántica del NestJS: éxito/error individual).</summary>
public sealed record ConfirmarResultado(string PedidoId, bool Exito, string? Error = null);

/// <summary>
/// Excepción de dominio del flujo de caja. El controller la mapea a 400/404, replicando los
/// <c>BadRequestException</c> / <c>NotFoundException</c> de NestJS. Mismo patrón que <c>PedidoException</c>.
/// </summary>
public sealed class CajaException : Exception
{
    public int StatusCode { get; }

    private CajaException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public static CajaException BadRequest(string message) => new(400, message);

    public static CajaException NotFound(string message) => new(404, message);
}

/// <summary>
/// Operaciones transaccionales de caja: registrar el pago de un pedido (crea la ENTRADA y marca el pedido
/// PAGADO en una sola transacción) y el batch de confirmación. Los movimientos manuales, la anulación y las
/// lecturas (resumen, pendientes de cobro, etc.) son mutaciones cortas / reads y viven en el controller.
/// </summary>
public interface ICajaService
{
    /// <summary>
    /// Registra el pago de un pedido: valida (existe, no cancelado/anulado, sin movimiento previo), crea el
    /// <c>CajaMovimiento</c> ENTRADA y marca el pedido <c>PAGADO</c> + <c>cuentaAbierta=false</c>, todo dentro de
    /// una transacción. <paramref name="gananciaRepartidor"/> por defecto = costo de envío del pedido.
    /// </summary>
    Task<CajaMovimientoDto> RegistrarPagoPedidoAsync(
        string pedidoId,
        string confirmadoPor,
        string negocioId,
        double? gananciaRepartidor = null,
        MetodoPago? metodoPago = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirma el pago de varios pedidos. Cada pedido va en su propia transacción (vía
    /// <see cref="RegistrarPagoPedidoAsync"/>): si uno falla no arrastra a los demás; devuelve el detalle
    /// éxito/error por id. Réplica de la semántica del NestJS.
    /// </summary>
    Task<IReadOnlyList<ConfirmarResultado>> ConfirmarPagosPendientesAsync(
        IReadOnlyList<string> pedidoIds,
        string confirmadoPor,
        string negocioId,
        CancellationToken cancellationToken = default);
}
