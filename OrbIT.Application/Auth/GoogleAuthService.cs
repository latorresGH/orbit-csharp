using Microsoft.EntityFrameworkCore;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Auth;

/// <summary>
/// Implementación de <see cref="IGoogleAuthService"/>. El OTT se persiste hasheado (SHA-256) en
/// <c>TempToken</c>: el valor crudo sólo viaja al frontend en el redirect y se canjea una única vez. Las
/// queries van con <c>IgnoreQueryFilters</c> porque el flujo corre fuera de un tenant resuelto (no hay JWT
/// todavía) y <c>TempToken</c> no tiene Global Query Filter.
/// </summary>
public sealed class GoogleAuthService : IGoogleAuthService
{
    /// <summary>TTL del OTT: 30 segundos (paridad con el NestJS).</summary>
    private static readonly TimeSpan OttTtl = TimeSpan.FromSeconds(30);

    private readonly OrbitDbContext _db;
    private readonly IJwtTokenService _jwt;

    public GoogleAuthService(OrbitDbContext db, IJwtTokenService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    public async Task<GoogleLoginResult> ResolveLoginAsync(string email, string nombre, string? slug, CancellationToken ct = default)
    {
        var normalizedEmail = NormalizeEmail(email);

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var slugNorm = slug.Trim().ToLowerInvariant();
            var user = await _db.Users.IgnoreQueryFilters()
                .Where(u => u.Email == normalizedEmail
                    && u.Role == Role.ADMIN
                    && u.Activo
                    && u.Negocio!.Slug == slugNorm)
                .Select(u => new { u.Id, u.NegocioId })
                .FirstOrDefaultAsync(ct);

            if (user is not null)
            {
                var ott = await GenerarOttAsync(user.Id, user.NegocioId, ct);
                return new GoogleLoginResult(GoogleLoginTipo.Login, ott, normalizedEmail, nombre);
            }
        }

        return new GoogleLoginResult(GoogleLoginTipo.Registro, null, normalizedEmail, nombre);
    }

    public async Task<GoogleSessionData?> ConsumeOttAsync(string rawOtt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawOtt))
        {
            return null;
        }

        var hash = _jwt.ComputeTokenHash(rawOtt);
        var now = Now();

        var token = await _db.TempTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && !t.Usada && t.ExpiresAt > now, ct);
        if (token is null)
        {
            return null;
        }

        // Single-use: se marca consumido de inmediato.
        token.Usada = true;

        var user = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == token.UserId && u.Activo)
            .Select(u => new { u.Id, u.Role, u.NegocioId, u.Email, u.Nombre })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            // El token queda marcado como usado igual (no reutilizable) aunque el usuario ya no exista/esté inactivo.
            await _db.SaveChangesAsync(ct);
            return null;
        }

        await _db.SaveChangesAsync(ct);
        return new GoogleSessionData(user.Id, user.Role, user.NegocioId, user.Email, user.Nombre);
    }

    private async Task<string> GenerarOttAsync(string userId, string? negocioId, CancellationToken ct)
    {
        // Token opaco de 128 bits (equivalente al randomUUID del NestJS). Sólo se persiste su hash.
        var rawOtt = Guid.NewGuid().ToString("N");
        var now = Now();

        _db.TempTokens.Add(new TempToken
        {
            Id = Guid.NewGuid().ToString(),
            TokenHash = _jwt.ComputeTokenHash(rawOtt),
            UserId = userId,
            NegocioId = negocioId,
            ExpiresAt = now.Add(OttTtl),
            Usada = false,
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(ct);

        return rawOtt;
    }

    private static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    // Columnas "timestamp without time zone": Npgsql exige Kind=Unspecified.
    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
