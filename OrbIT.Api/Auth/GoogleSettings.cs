namespace OrbIT.Api.Auth;

/// <summary>
/// Configuración de Google OAuth (sección "Google" de appsettings). <see cref="ClientId"/> y
/// <see cref="ClientSecret"/> van con placeholder en el appsettings.json commiteado y con el valor real en
/// appsettings.Development.json (gitignoreado) — mismo patrón que <c>MercadoPagoSettings</c>. Si no hay
/// credenciales reales, el handler de Google NO se registra (así los tests/CI arrancan sin credenciales) y el
/// endpoint <c>GET /auth/google</c> responde 501.
/// </summary>
public sealed class GoogleSettings
{
    public const string SectionName = "Google";

    /// <summary>Client ID de la consola de Google Cloud. Placeholder en el commiteado.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client Secret de la consola de Google Cloud. Placeholder en el commiteado.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Ruta interna que intercepta el handler de Google para procesar el <c>code</c>. Es la URI que hay que
    /// registrar como "Authorized redirect URI" en la consola de Google (ej: <c>https://api.tudominio/auth/google/signin-callback</c>).
    /// No confundir con <c>GET /auth/google/callback</c> (la acción del controller que redirige al frontend).
    /// </summary>
    public string CallbackPath { get; set; } = "/auth/google/signin-callback";

    /// <summary>Hay credenciales reales (no placeholder <c>__...__</c>): recién ahí se registra el handler.</summary>
    public bool TieneCredenciales =>
        !string.IsNullOrWhiteSpace(ClientId) && !ClientId.StartsWith("__", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ClientSecret) && !ClientSecret.StartsWith("__", StringComparison.Ordinal);
}
