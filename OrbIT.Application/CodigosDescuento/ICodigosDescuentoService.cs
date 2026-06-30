namespace OrbIT.Application.CodigosDescuento;

/// <summary>Datos del descuento (tipo + valor) cuando el código es válido.</summary>
public sealed record DescuentoInfo(string Tipo, double Valor);

/// <summary>Snapshot del código válido devuelto al cliente.</summary>
public sealed record CodigoInfo(
    string Id,
    string Codigo,
    string? Descripcion,
    string TipoDescuento,
    double Valor,
    string? ProductoId,
    int? UsosMaximos);

/// <summary>
/// Resultado de validar un código. Es un <b>resultado</b>, no una excepción: <c>Valido=false</c> + un
/// <c>Error</c> legible cuando no aplica (igual que el NestJS, que devuelve <c>{ valido:false, error }</c>
/// con 200). Cuando es válido, trae <see cref="Codigo"/> y <see cref="Descuento"/>.
/// </summary>
public sealed record ValidacionCodigo(
    bool Valido,
    string? Error = null,
    CodigoInfo? Codigo = null,
    DescuentoInfo? Descuento = null);

/// <summary>
/// Validación de códigos de descuento reutilizable (la consume el endpoint público
/// <c>POST /codigos-descuento/validar</c> y, más adelante, <c>crearPedido</c> del PedidosController).
/// Vive en OrbIT.Application igual que <c>IOfertasCalculatorService</c>: inyectable, scoped, comparte el
/// <c>OrbitDbContext</c> del request.
/// </summary>
public interface ICodigosDescuentoService
{
    /// <summary>Valida un código para el negocio (y opcionalmente para un producto). No lanza; devuelve el resultado.</summary>
    Task<ValidacionCodigo> ValidarAsync(
        string codigo,
        string negocioId,
        string? productoId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Incrementa <c>usosActuales</c> del código (lo llama el PedidosController al confirmar el pedido).</summary>
    Task IncrementarUsoAsync(string codigoId, CancellationToken cancellationToken = default);
}
