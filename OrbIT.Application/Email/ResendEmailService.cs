using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrbIT.Application.Email;

/// <summary>
/// Implementación real de <see cref="IEmailService"/> sobre la API HTTP de Resend
/// (<c>POST https://api.resend.com/emails</c>). Reemplaza a <c>EmailServiceStub</c> en el registro de DI, pero
/// incorpora el fallback del stub: si no hay <see cref="ResendSettings.ApiKey"/> real configurada (dev/test o
/// CI con el placeholder), loguea el código en vez de mandarlo, así el flujo de registro sigue funcionando sin
/// credenciales.
///
/// <para>El <see cref="HttpClient"/> se obtiene de <see cref="IHttpClientFactory"/> (registrado en Program.cs)
/// para reusar el pool de conexiones y respetar el ciclo de vida del handler. Best-effort en el sentido de que
/// una falla de red se loguea, pero —a diferencia de la notificación de SignalR— acá sí se propaga: si el mail
/// no salió, el registro debe poder reportarlo (el llamador decide).</para>
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    /// <summary>Nombre del <see cref="HttpClient"/> nombrado que registra Program.cs con la BaseAddress de Resend.</summary>
    public const string HttpClientName = "resend";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResendSettings _settings;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        IHttpClientFactory httpClientFactory,
        IOptions<ResendSettings> settings,
        ILogger<ResendEmailService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task EnviarCodigoVerificacionAsync(string email, string codigo, string nombreNegocio, CancellationToken cancellationToken = default)
    {
        // Fallback stub: sin API key real, se comporta como EmailServiceStub (loguea, no manda).
        if (!_settings.TieneApiKey)
        {
            _logger.LogInformation(
                "[EmailStub] Código de verificación para {Email} (negocio {Negocio}): {Codigo}",
                email, nombreNegocio, codigo);
            return;
        }

        var payload = new ResendEmailPayload(
            From: _settings.From,
            To: new[] { email },
            Subject: $"Tu código de verificación de {nombreNegocio}",
            Html: ConstruirHtml(codigo, nombreNegocio));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        using var response = await client.PostAsJsonAsync("emails", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "[Resend] Falló el envío del código a {Email} (negocio {Negocio}): {Status} {Body}",
                email, nombreNegocio, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode(); // propaga: el registro debe poder reportar que el mail no salió.
        }

        _logger.LogInformation("[Resend] Código de verificación enviado a {Email} (negocio {Negocio}).", email, nombreNegocio);
    }

    /// <summary>HTML simple y limpio: el código de 6 dígitos bien visible, el nombre del negocio y el aviso de expiración.</summary>
    private static string ConstruirHtml(string codigo, string nombreNegocio) => $@"
<div style=""font-family: Arial, Helvetica, sans-serif; max-width: 480px; margin: 0 auto; padding: 32px 24px; color: #1a1a1a;"">
  <h1 style=""font-size: 20px; margin: 0 0 8px;"">Verificá tu email</h1>
  <p style=""font-size: 15px; line-height: 1.5; margin: 0 0 24px; color: #444;"">
    Usá este código para completar el registro de <strong>{System.Net.WebUtility.HtmlEncode(nombreNegocio)}</strong> en Orb.IT:
  </p>
  <div style=""font-size: 36px; font-weight: 700; letter-spacing: 8px; text-align: center; padding: 20px; background: #f4f4f5; border-radius: 12px; color: #111;"">
    {System.Net.WebUtility.HtmlEncode(codigo)}
  </div>
  <p style=""font-size: 13px; line-height: 1.5; margin: 24px 0 0; color: #888;"">
    El código expira en <strong>15 minutos</strong>. Si no solicitaste este registro, podés ignorar este mensaje.
  </p>
</div>";

    /// <summary>Body del POST a Resend. Serializado en snake_case porque la API espera <c>from/to/subject/html</c>.</summary>
    private sealed record ResendEmailPayload(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html);
}
