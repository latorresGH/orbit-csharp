using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Auth;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    private const string RefreshCookie = "refresh_token";

    // Mensaje genérico: no revela si fue el email, el negocio o la contraseña (anti-enumeración).
    private static readonly object CredencialesInvalidas = new { message = "Credenciales inválidas." };

    private readonly OrbitDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwt;
    private readonly ISessionIssuer _session;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        OrbitDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwt,
        ISessionIssuer session,
        ILogger<AuthController> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwt = jwt;
        _session = session;
        _logger = logger;
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(CredencialesInvalidas);
        }

        // Las queries van con IgnoreQueryFilters porque todavía no hay tenant resuelto
        // (no hay JWT); el scoping por negocio se hace a mano acá.
        User? user;
        if (string.IsNullOrWhiteSpace(request.NegocioSlug))
        {
            // Sin slug => login de SUPERADMIN (NegocioId nulo, no pertenece a un negocio).
            user = await _db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email == request.Email && u.Role == Role.SUPERADMIN);
        }
        else
        {
            var negocio = await _db.Negocios.IgnoreQueryFilters()
                .FirstOrDefaultAsync(n => n.Slug == request.NegocioSlug);
            if (negocio is null)
            {
                return Unauthorized(CredencialesInvalidas);
            }

            user = await _db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email == request.Email && u.NegocioId == negocio.Id);
        }

        if (user is null)
        {
            return Unauthorized(CredencialesInvalidas);
        }

        // Lockout progresivo: si sigue bloqueado, ni se verifica la contraseña.
        if (user.BloqueadoLoginHasta is { } bloqueadoHasta && bloqueadoHasta > DateTime.UtcNow)
        {
            _logger.LogWarning("Login bloqueado por lockout para el usuario {UserId}.", user.Id);
            return StatusCode(StatusCodes.Status423Locked, new
            {
                message = "Cuenta bloqueada temporalmente por demasiados intentos fallidos.",
                bloqueadoHasta,
            });
        }

        if (!user.Activo)
        {
            return Unauthorized(CredencialesInvalidas);
        }

        if (!_passwordHasher.Verify(request.Password, user.Password))
        {
            user.IntentosLogin++;
            AplicarLockoutProgresivo(user);
            await _db.SaveChangesAsync();
            _logger.LogWarning(
                "Intento de login fallido #{Intentos} para el usuario {UserId}.",
                user.IntentosLogin, user.Id);
            return Unauthorized(CredencialesInvalidas);
        }

        // Éxito: se limpia el estado de lockout.
        user.IntentosLogin = 0;
        user.BloqueadoLoginHasta = null;

        await _session.IssueAsync(Response, user.Id, user.Role, user.NegocioId);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Login exitoso para el usuario {UserId}.", user.Id);
        return Ok(UserPayload(user));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var token) || string.IsNullOrEmpty(token))
        {
            return Unauthorized(CredencialesInvalidas);
        }

        if (_jwt.ValidateRefreshToken(token) is null)
        {
            return Unauthorized(CredencialesInvalidas);
        }

        var hash = _jwt.ComputeTokenHash(token);
        var stored = await _db.RefreshTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TokenHash == hash);

        if (stored is null || stored.Revocado || stored.ExpiresAt <= DateTime.UtcNow)
        {
            return Unauthorized(CredencialesInvalidas);
        }

        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId);
        if (user is null || !user.Activo)
        {
            return Unauthorized(CredencialesInvalidas);
        }

        // Rotación: se revoca el refresh usado y se emite uno nuevo.
        stored.Revocado = true;
        await _session.IssueAsync(Response, user.Id, user.Role, user.NegocioId);
        await _db.SaveChangesAsync();

        return Ok(UserPayload(user));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(RefreshCookie, out var token) && !string.IsNullOrEmpty(token))
        {
            var hash = _jwt.ComputeTokenHash(token);
            var stored = await _db.RefreshTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.TokenHash == hash);
            if (stored is not null && !stored.Revocado)
            {
                stored.Revocado = true;
                await _db.SaveChangesAsync();
            }
        }

        _session.ClearCookies(Response);
        return Ok(new { message = "Sesión cerrada." });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        sub = User.FindFirst("sub")?.Value,
        role = User.FindFirst("role")?.Value,
        negocioId = User.FindFirst("negocioId")?.Value,
    });

    private static void AplicarLockoutProgresivo(User user)
    {
        // Mismos umbrales que NestJS: 5 -> 5min, 10 -> 30min, 15 -> 24h. Contador acumulativo.
        var now = DateTime.UtcNow;
        DateTime? bloqueoHasta = user.IntentosLogin switch
        {
            >= 15 => now.AddHours(24),
            >= 10 => now.AddMinutes(30),
            >= 5 => now.AddMinutes(5),
            _ => null,
        };

        if (bloqueoHasta is { } hasta)
        {
            user.BloqueadoLoginHasta = DateTime.SpecifyKind(hasta, DateTimeKind.Unspecified);
        }
    }

    private static object UserPayload(User user) => new
    {
        id = user.Id,
        email = user.Email,
        nombre = user.Nombre,
        role = user.Role.ToString(),
        negocioId = user.NegocioId,
    };
}

/// <summary>Body del login. <c>NegocioSlug</c> es opcional: vacío => login de SUPERADMIN.</summary>
public sealed record LoginRequest(string Email, string Password, string? NegocioSlug);
