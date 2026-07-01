namespace OrbIT.Api.Billing;

/// <summary>
/// Configuración de MercadoPago (sección "MercadoPago" de appsettings). El <see cref="AccessToken"/> y el
/// <see cref="WebhookSecret"/> van con placeholder en el appsettings.json commiteado y con el valor real de
/// prueba en appsettings.Development.json (gitignoreado). Las URLs son opcionales: si faltan, el checkout no
/// manda back_urls/notification_url (MP las omite sin error).
/// </summary>
public sealed class MercadoPagoSettings
{
    public const string SectionName = "MercadoPago";

    /// <summary>Access token de la cuenta MP (Bearer para el SDK). Placeholder en el commiteado.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Secret configurado en el panel de MP para validar la firma del webhook (x-signature).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>URL pública a la que MP manda las notificaciones de pago (POST /billing/webhook).</summary>
    public string? NotificationUrl { get; set; }

    public string? BackUrlSuccess { get; set; }
    public string? BackUrlFailure { get; set; }
    public string? BackUrlPending { get; set; }

    public bool TieneAccessToken => !string.IsNullOrWhiteSpace(AccessToken) && !AccessToken.StartsWith("__", StringComparison.Ordinal);
    public bool TieneWebhookSecret => !string.IsNullOrWhiteSpace(WebhookSecret) && !WebhookSecret.StartsWith("__", StringComparison.Ordinal);
}
