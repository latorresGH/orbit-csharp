using OrbIT.Domain.Enums;

namespace OrbIT.Application.Auth;

/// <summary>Qué resolvió el callback de Google para un email dado.</summary>
public enum GoogleLoginTipo
{
    /// <summary>Existe un ADMIN para ese email+negocio: se generó un OTT para canjear por sesión.</summary>
    Login,

    /// <summary>No hay cuenta: el frontend debe completar el alta (<c>/registro/completar</c>).</summary>
    Registro,
}

/// <summary>
/// Resultado del callback. En <see cref="GoogleLoginTipo.Login"/> viene <see cref="Ott"/> (token crudo, de un
/// solo uso, TTL 30s). En <see cref="GoogleLoginTipo.Registro"/> vienen <see cref="Email"/>/<see cref="Nombre"/>
/// normalizados para prellenar el alta.
/// </summary>
public sealed record GoogleLoginResult(GoogleLoginTipo Tipo, string? Ott, string Email, string Nombre);

/// <summary>Datos para emitir la sesión tras consumir un OTT válido (mismo shape que usa el controller para IssueAsync).</summary>
public sealed record GoogleSessionData(string UserId, Role Role, string? NegocioId, string Email, string Nombre);

/// <summary>
/// Lógica del OTT de Google OAuth, persistido en la tabla <c>TempToken</c> (single-use + TTL) para resolver el
/// problema del store en memoria del NestJS. No depende de ASP.NET: el controller orquesta el redirect/cookies.
/// </summary>
public interface IGoogleAuthService
{
    /// <summary>
    /// Réplica de <c>loginConGoogle</c>: si hay <paramref name="slug"/> y existe un <c>User</c> ADMIN activo con
    /// ese email en ese negocio, genera y persiste un OTT (TTL 30s) y devuelve <see cref="GoogleLoginTipo.Login"/>.
    /// En cualquier otro caso devuelve <see cref="GoogleLoginTipo.Registro"/> con el email/nombre normalizados.
    /// </summary>
    Task<GoogleLoginResult> ResolveLoginAsync(string email, string nombre, string? slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consume un OTT: válido sólo si existe, no fue usado y no expiró. Lo marca como usado (single-use) y
    /// devuelve los datos de sesión. Devuelve <c>null</c> si es inválido/expirado/ya usado o el usuario no existe
    /// o está inactivo.
    /// </summary>
    Task<GoogleSessionData?> ConsumeOttAsync(string rawOtt, CancellationToken cancellationToken = default);
}
