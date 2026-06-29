using System.Security.Claims;
using OrbIT.Domain.Enums;

namespace OrbIT.Application.Auth;

/// <summary>
/// Emisión y validación de tokens JWT firmados. Access y refresh usan claves HMAC
/// separadas. Los claims emitidos son <c>sub</c> (userId), <c>role</c> y, si aplica,
/// <c>negocioId</c> — este último es el que consume el <c>HttpTenantProvider</c>.
/// </summary>
public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(string userId, Role role, string? negocioId);

    /// <summary>
    /// Refresh firmado con duración diferenciada por rol: 7 días para ADMIN/SUPERADMIN,
    /// 12 horas para roles operativos (TRABAJADOR/DELIVERY).
    /// </summary>
    (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken(string userId, Role role, string? negocioId);

    /// <summary>Hash determinístico (SHA-256) del token, para persistir y poder revocar por lookup.</summary>
    string ComputeTokenHash(string token);

    /// <summary>Valida firma/issuer/audience/expiración de un refresh token. Devuelve null si es inválido.</summary>
    ClaimsPrincipal? ValidateRefreshToken(string token);
}
