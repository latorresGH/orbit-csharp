namespace OrbIT.Application.Email;

/// <summary>
/// Configuración de Resend (sección "Resend" de appsettings). La <see cref="ApiKey"/> va con placeholder en el
/// appsettings.json commiteado y con el valor real en appsettings.Development.json (gitignoreado), mismo patrón
/// que <c>MercadoPagoSettings</c>. Si no hay API key real, <see cref="ResendEmailService"/> cae al stub
/// (loguea en vez de mandar) para que dev/test funcionen sin credenciales.
/// </summary>
public sealed class ResendSettings
{
    public const string SectionName = "Resend";

    /// <summary>API key de Resend (Bearer). Placeholder en el commiteado.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Remitente de los emails (ej. "Orb.IT &lt;no-reply@tudominio.com&gt;"). Debe ser un dominio verificado en Resend.</summary>
    public string From { get; set; } = "Orb.IT <onboarding@resend.dev>";

    /// <summary>true sólo si hay una API key real configurada (no vacía ni placeholder <c>__...</c>).</summary>
    public bool TieneApiKey => !string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.StartsWith("__", StringComparison.Ordinal);
}
