using Microsoft.Extensions.Logging;

namespace OrbIT.Application.Email;

/// <summary>
/// Stub de <see cref="IEmailService"/>: no manda mails, loguea el código de verificación (nivel Information)
/// para poder completar el flujo de registro en desarrollo y tests. El wiring real de SMTP/proveedor queda
/// como pendiente conocido — cuando exista, se reemplaza el registro en <c>Program.cs</c> por la implementación
/// real sin tocar el resto del módulo. Mismo patrón que <c>DemoraServiceStub</c>.
/// </summary>
public sealed class EmailServiceStub : IEmailService
{
    private readonly ILogger<EmailServiceStub> _logger;

    public EmailServiceStub(ILogger<EmailServiceStub> logger) => _logger = logger;

    public Task EnviarCodigoVerificacionAsync(string email, string codigo, string nombreNegocio, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[EmailStub] Código de verificación para {Email} (negocio {Negocio}): {Codigo}",
            email, nombreNegocio, codigo);
        return Task.CompletedTask;
    }
}
