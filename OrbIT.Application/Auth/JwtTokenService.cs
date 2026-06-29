using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrbIT.Domain.Enums;

namespace OrbIT.Application.Auth;

/// <inheritdoc />
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(string userId, Role role, string? negocioId)
    {
        var expires = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpirationMinutes);
        var claims = BaseClaims(userId, negocioId);
        claims.Add(new Claim("role", role.ToString()));
        return (BuildToken(claims, _settings.AccessTokenSecret, expires), expires);
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken(string userId, Role role, string? negocioId)
    {
        var expires = DateTime.UtcNow.Add(RefreshLifetime(role));
        var claims = BaseClaims(userId, negocioId);
        return (BuildToken(claims, _settings.RefreshTokenSecret, expires), expires);
    }

    public string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public ClaimsPrincipal? ValidateRefreshToken(string token)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            return handler.ValidateToken(token, BuildValidationParameters(), out _);
        }
        catch
        {
            // Firma inválida, expirado, issuer/audience incorrectos, token corrupto, etc.
            return null;
        }
    }

    private TimeSpan RefreshLifetime(Role role) => role switch
    {
        Role.TRABAJADOR or Role.DELIVERY => TimeSpan.FromHours(_settings.RefreshTokenExpirationHoursOperativo),
        _ => TimeSpan.FromDays(_settings.RefreshTokenExpirationDays),
    };

    private static List<Claim> BaseClaims(string userId, string? negocioId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (!string.IsNullOrEmpty(negocioId))
        {
            claims.Add(new Claim("negocioId", negocioId));
        }

        return claims;
    }

    private string BuildToken(IEnumerable<Claim> claims, string secret, DateTime expiresUtc)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresUtc,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters BuildValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _settings.Issuer,
        ValidateAudience = true,
        ValidAudience = _settings.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.RefreshTokenSecret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
