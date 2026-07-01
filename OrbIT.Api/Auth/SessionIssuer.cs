using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Auth;

/// <summary>
/// Implementación de <see cref="ISessionIssuer"/>. Scoped: comparte el <c>OrbitDbContext</c> del request para
/// persistir el refresh token junto con los demás cambios del endpoint. El access token viaja en cookie
/// HttpOnly (mismo esquema que el resto del sistema): <c>Secure</c> salvo en Development, <c>SameSite=Lax</c>.
/// </summary>
public sealed class SessionIssuer : ISessionIssuer
{
    private const string AccessCookie = "access_token";
    private const string RefreshCookie = "refresh_token";
    private const string AccessCookiePath = "/";
    private const string RefreshCookiePath = "/auth";

    private readonly OrbitDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IWebHostEnvironment _env;

    public SessionIssuer(OrbitDbContext db, IJwtTokenService jwt, IWebHostEnvironment env)
    {
        _db = db;
        _jwt = jwt;
        _env = env;
    }

    public async Task IssueAsync(HttpResponse response, string userId, Role role, string? negocioId)
    {
        var (accessToken, accessExpUtc) = _jwt.GenerateAccessToken(userId, role, negocioId);
        var (refreshToken, refreshExpUtc) = _jwt.GenerateRefreshToken(userId, role, negocioId);

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid().ToString(),
            TokenHash = _jwt.ComputeTokenHash(refreshToken),
            UserId = userId,
            NegocioId = negocioId,
            // Columnas "timestamp without time zone": Npgsql exige Kind=Unspecified.
            ExpiresAt = DateTime.SpecifyKind(refreshExpUtc, DateTimeKind.Unspecified),
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            Revocado = false,
        });

        SetCookie(response, AccessCookie, accessToken, accessExpUtc, AccessCookiePath);
        SetCookie(response, RefreshCookie, refreshToken, refreshExpUtc, RefreshCookiePath);

        await Task.CompletedTask;
    }

    public void ClearCookies(HttpResponse response)
    {
        var secure = !_env.IsDevelopment();
        response.Cookies.Delete(AccessCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = AccessCookiePath,
        });
        response.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = RefreshCookiePath,
        });
    }

    private void SetCookie(HttpResponse response, string name, string value, DateTime expiresUtc, string path) =>
        response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = path,
            Expires = new DateTimeOffset(expiresUtc, TimeSpan.Zero),
        });
}
