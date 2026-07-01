using OrbIT.Domain.Enums;

namespace OrbIT.Api.Auth;

/// <summary>
/// Emisión y limpieza de la sesión por cookies (access + refresh HttpOnly). Centraliza lo que antes vivía
/// como método privado en <c>AuthController</c>, para que también lo use <c>NegocioController</c> al verificar
/// el email (única fuente de verdad del manejo de cookies/refresh).
/// </summary>
public interface ISessionIssuer
{
    /// <summary>
    /// Genera access + refresh, persiste el hash del refresh (para rotación/revocación) vía el DbContext del
    /// request y setea las cookies en <paramref name="response"/>. <b>No</b> hace <c>SaveChanges</c>: lo
    /// coordina el endpoint para agrupar con sus otros cambios.
    /// </summary>
    Task IssueAsync(HttpResponse response, string userId, Role role, string? negocioId);

    /// <summary>Limpia las cookies de sesión (logout / cierre de cuenta).</summary>
    void ClearCookies(HttpResponse response);
}
