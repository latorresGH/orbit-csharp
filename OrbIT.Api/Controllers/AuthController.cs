using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrbIT.Api.Auth;
using OrbIT.Api.Contracts.Auth;
using OrbIT.Api.Contracts.Negocios;
using OrbIT.Application.Auth;
using OrbIT.Application.Negocios;
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
    private readonly IGoogleAuthService _googleAuth;
    private readonly INegocioService _negocio;
    private readonly GoogleSettings _googleSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        OrbitDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwt,
        ISessionIssuer session,
        IGoogleAuthService googleAuth,
        INegocioService negocio,
        IOptions<GoogleSettings> googleSettings,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwt = jwt;
        _session = session;
        _googleAuth = googleAuth;
        _negocio = negocio;
        _googleSettings = googleSettings.Value;
        _configuration = configuration;
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

    // ═════════════════════════════════════════════════════════════════════════
    // Google OAuth
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Inicia el flujo OAuth: redirige a Google. El <c>slug</c> (a qué negocio loguear) viaja en
    /// <see cref="AuthenticationProperties.Items"/>, protegido por el state del handler (no hace falta store de
    /// CSRF). Google vuelve al <c>CallbackPath</c> del handler y de ahí a <see cref="GoogleCallback"/>.
    /// </summary>
    [HttpGet("google")]
    public IActionResult GoogleStart([FromQuery] string? slug = null)
    {
        if (!_googleSettings.TieneCredenciales)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { message = "Google OAuth no está configurado." });
        }

        var props = new AuthenticationProperties { RedirectUri = "/auth/google/callback" };
        if (!string.IsNullOrWhiteSpace(slug))
        {
            props.Items["slug"] = slug.Trim().ToLowerInvariant();
        }

        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Callback tras validar el roundtrip con Google. Lee la identidad de la cookie externa, resuelve
    /// login-vs-registro y redirige al frontend: con OTT (<c>/auth/callback?ott=</c>) si hay cuenta, o a
    /// <c>/registro/completar</c> si hay que crearla. No emite sesión acá (eso pasa en el exchange).
    /// </summary>
    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback()
    {
        var frontendUrl = FrontendUrl();

        var result = await HttpContext.AuthenticateAsync(GoogleOAuth.ExternalScheme);
        // La cookie externa ya cumplió su función: se limpia siempre.
        await HttpContext.SignOutAsync(GoogleOAuth.ExternalScheme);

        if (!result.Succeeded || result.Principal is null)
        {
            return Redirect($"{frontendUrl}/login?error=google");
        }

        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return Redirect($"{frontendUrl}/login?error=google");
        }
        var nombre = result.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        string? slug = null;
        result.Properties?.Items.TryGetValue("slug", out slug);

        var login = await _googleAuth.ResolveLoginAsync(email, nombre, slug);
        if (login.Tipo == GoogleLoginTipo.Login)
        {
            return Redirect($"{frontendUrl}/auth/callback?ott={Uri.EscapeDataString(login.Ott!)}");
        }

        var query = $"email={Uri.EscapeDataString(login.Email)}&nombre={Uri.EscapeDataString(login.Nombre)}";
        return Redirect($"{frontendUrl}/registro/completar?{query}");
    }

    /// <summary>
    /// Canjea el OTT (un solo uso, TTL 30s) por cookies de sesión. Devuelve 401 si el OTT es inválido, expiró o
    /// ya fue usado.
    /// </summary>
    [HttpPost("google/exchange")]
    public async Task<IActionResult> GoogleExchange([FromBody] GoogleExchangeRequest request)
    {
        var data = await _googleAuth.ConsumeOttAsync(request.Ott);
        if (data is null)
        {
            return Unauthorized(new { message = "El enlace de autenticación expiró o ya fue usado." });
        }

        await _session.IssueAsync(Response, data.UserId, data.Role, data.NegocioId);
        await _db.SaveChangesAsync();

        return Ok(new { user = new UsuarioResponse(data.UserId, data.Email, data.Nombre, data.Role.ToString()) });
    }

    /// <summary>
    /// Crea negocio + admin vía Google (sin contraseña, email verificado) y emite la sesión de inmediato.
    /// </summary>
    [HttpPost("google/registro")]
    [EnableRateLimiting("registro")]
    public async Task<IActionResult> GoogleRegistro([FromBody] GoogleRegistroRequest request)
    {
        try
        {
            var input = new RegistroGoogleInput(request.NombreNegocio, request.Slug, request.Nombre, request.Email);
            var r = await _negocio.RegistrarNuevoNegocioGoogleAsync(input);
            await _session.IssueAsync(Response, r.UserId, r.Role, r.NegocioId);
            await _db.SaveChangesAsync();
            return Ok(new { user = new UsuarioResponse(r.User.Id, r.User.Email, r.User.Nombre, r.User.Role.ToString()) });
        }
        catch (NegocioException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    /// <summary>URL del frontend para los redirects: primer origen de <c>Cors:AllowedOrigins</c> (equivale al
    /// <c>getFrontendUrl</c> del NestJS, que usaba el primer valor de <c>FRONTEND_URL</c>).</summary>
    private string FrontendUrl()
    {
        var origins = _configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        var url = origins is { Length: > 0 } ? origins[0] : "http://localhost:3000";
        return url.TrimEnd('/');
    }

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
