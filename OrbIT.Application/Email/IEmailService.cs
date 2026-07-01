namespace OrbIT.Application.Email;

/// <summary>
/// Envío de emails transaccionales. Hoy sólo el código de verificación del registro de negocios. La
/// implementación real (SMTP/proveedor) queda pendiente; en su lugar hay un stub que loguea el código
/// (mismo patrón que <c>IDemoraService</c> en Pedidos). Ver <see cref="EmailServiceStub"/>.
/// </summary>
public interface IEmailService
{
    /// <summary>Envía el código de verificación de email al admin recién registrado.</summary>
    Task EnviarCodigoVerificacionAsync(string email, string codigo, string nombreNegocio, CancellationToken cancellationToken = default);
}
