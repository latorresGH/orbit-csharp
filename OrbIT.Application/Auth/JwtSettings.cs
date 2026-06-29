namespace OrbIT.Application.Auth;

/// <summary>
/// Settings de JWT bindeados desde la sección <c>Jwt</c> de la configuración.
/// Los secrets se cargan de <c>appsettings.Development.json</c> (gitignoreado) o de
/// variables de entorno; en <c>appsettings.json</c> (commiteado) sólo hay placeholders.
/// </summary>
public sealed class JwtSettings
{
    public string Issuer { get; set; } = "orbit-api";

    public string Audience { get; set; } = "orbit-clients";

    /// <summary>Clave HMAC-SHA256 del access token (≥ 32 bytes).</summary>
    public string AccessTokenSecret { get; set; } = string.Empty;

    /// <summary>Clave HMAC-SHA256 del refresh token, distinta de la del access (≥ 32 bytes).</summary>
    public string RefreshTokenSecret { get; set; } = string.Empty;

    public int AccessTokenExpirationMinutes { get; set; } = 15;

    /// <summary>Duración del refresh para ADMIN/SUPERADMIN.</summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;

    /// <summary>Duración del refresh para roles operativos (TRABAJADOR/DELIVERY, terminal compartida).</summary>
    public int RefreshTokenExpirationHoursOperativo { get; set; } = 12;
}
