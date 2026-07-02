namespace OrbIT.Api.Auth;

/// <summary>Constantes compartidas del flujo Google OAuth entre <c>Program</c> y <c>AuthController</c>.</summary>
public static class GoogleOAuth
{
    /// <summary>
    /// Esquema de la cookie externa temporal donde el handler de Google deja la identidad (email+nombre) entre
    /// el challenge y el callback. El controller la lee con <c>AuthenticateAsync</c> y la limpia con <c>SignOutAsync</c>.
    /// </summary>
    public const string ExternalScheme = "External";
}
