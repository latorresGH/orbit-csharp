using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Auth;
using OrbIT.Api.Contracts.Negocios;
using OrbIT.Application.Negocios;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Módulo Negocio: onboarding (registro público con verificación de email por código + alta manual de
/// SUPERADMIN), perfil/estado del negocio propio del ADMIN, cierre de cuenta con período de gracia, y la
/// gestión SUPERADMIN de todos los negocios. Lo transaccional/pesado (registro, verificación, purga) vive en
/// <see cref="INegocioService"/>; el resto (reads y updates de un solo campo) acá.
///
/// Los flujos públicos/SUPERADMIN operan fuera de un tenant resuelto → usan <c>IgnoreQueryFilters</c> (el GQF
/// de <c>Negocio</c> es por su propia Id). La verificación de email emite sesión reusando
/// <see cref="ISessionIssuer"/> (misma vía que <c>AuthController</c>).
/// </summary>
[ApiController]
[Route("negocio")]
[Authorize]
public sealed class NegocioController : ControllerBase
{
    private const int GraciaDias = 16;

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly INegocioService _negocio;
    private readonly ISessionIssuer _session;

    public NegocioController(OrbitDbContext db, ITenantProvider tenant, INegocioService negocio, ISessionIssuer session)
    {
        _db = db;
        _tenant = tenant;
        _negocio = negocio;
        _session = session;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Públicos
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("registro")]
    [AllowAnonymous]
    [EnableRateLimiting("registro")]
    public async Task<IActionResult> Registro([FromBody] RegistroNegocioRequest request)
    {
        try
        {
            var input = new RegistroNegocioInput(request.NombreNegocio, request.Slug, request.NombreAdmin, request.Email, request.Password);
            var r = await _negocio.RegistrarNuevoNegocioAsync(input);
            return Ok(new RegistroPendienteResponse(true, r.Email, r.Slug));
        }
        catch (NegocioException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpPost("verificar-email")]
    [AllowAnonymous]
    public async Task<IActionResult> VerificarEmail([FromBody] VerificarEmailRequest request)
    {
        try
        {
            var r = await _negocio.VerificarEmailAsync(new VerificacionInput(request.Email, request.NegocioSlug, request.Codigo));
            await _session.IssueAsync(Response, r.UserId, r.Role, r.NegocioId);
            await _db.SaveChangesAsync();
            return Ok(new { user = new UsuarioResponse(r.User.Id, r.User.Email, r.User.Nombre, r.User.Role.ToString()) });
        }
        catch (NegocioException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpPost("reenviar-codigo")]
    [AllowAnonymous]
    [EnableRateLimiting("reenviar-codigo")]
    public async Task<IActionResult> ReenviarCodigo([FromBody] ReenviarCodigoRequest request)
    {
        try
        {
            await _negocio.ReenviarCodigoAsync(request.Email, request.NegocioSlug);
            return Ok(new { ok = true });
        }
        catch (NegocioException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpGet("check-slug")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckSlug([FromQuery] string? slug)
    {
        var s = slug ?? string.Empty;
        var existe = await _db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Slug == s);
        return Ok(new SlugDisponibleResponse(!existe));
    }

    [HttpGet("info-publica")]
    [AllowAnonymous]
    public async Task<IActionResult> InfoPublica([FromQuery] string? slug)
    {
        var s = slug ?? string.Empty;
        var info = await _db.Negocios.IgnoreQueryFilters()
            .Where(n => n.Slug == s)
            .Select(n => new InfoPublicaResponse(n.Id, n.Nombre, n.Slug, n.LogoUrl))
            .FirstOrDefaultAsync();
        return info is null ? NotFound(new { message = $"Negocio \"{s}\" no encontrado" }) : Ok(info);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Negocio propio (autenticado)
    // ═════════════════════════════════════════════════════════════════════════

    [HttpGet("mi-estado")]
    public async Task<IActionResult> MiEstado()
    {
        var negocio = await CargarMiNegocioAsync();
        if (negocio is null) return Forbid();

        var estado = PlanEstado.Calcular(negocio.Activo, negocio.Plan, negocio.TrialExpira);
        return Ok(new MiEstadoResponse(estado, PlanEstado.DiasRestantesTrial(estado, negocio.TrialExpira), negocio.TrialExpira));
    }

    [HttpGet("mi-perfil")]
    public async Task<IActionResult> GetMiPerfil()
    {
        var perfil = await ArmarMiPerfilAsync();
        return perfil is null ? Forbid() : Ok(perfil);
    }

    [HttpPatch("mi-perfil")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ActualizarMiPerfil([FromBody] ActualizarMiPerfilRequest request)
    {
        var negocioId = _tenant.NegocioId;
        var userId = ActorSub();
        if (string.IsNullOrEmpty(negocioId) || string.IsNullOrEmpty(userId)) return Forbid();

        var negocio = await _db.Negocios.FirstOrDefaultAsync(n => n.Id == negocioId);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        if (!string.IsNullOrWhiteSpace(request.Slug))
        {
            var nuevoSlug = request.Slug.Trim().ToLowerInvariant();
            if (nuevoSlug != negocio.Slug
                && await _db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Slug == nuevoSlug && n.Id != negocioId))
            {
                return Conflict(new { message = "Ese slug ya está en uso, elegí otro" });
            }
            negocio.Slug = nuevoSlug;
        }

        if (!string.IsNullOrWhiteSpace(request.NombreNegocio)) negocio.Nombre = request.NombreNegocio.Trim();

        // logoUrl: "" limpia el logo; con valor debe ser https. null = no tocar.
        if (request.LogoUrl is not null)
        {
            var url = request.LogoUrl.Trim();
            if (url.Length > 0 && !url.StartsWith("https://", StringComparison.Ordinal))
            {
                return BadRequest(new { message = "La URL del logo debe ser https" });
            }
            negocio.LogoUrl = url.Length == 0 ? null : url;
        }

        negocio.UpdatedAt = Now();

        if (!string.IsNullOrWhiteSpace(request.NombreAdmin))
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is not null) user.Nombre = request.NombreAdmin.Trim();
        }

        await _db.SaveChangesAsync();

        var perfil = await ArmarMiPerfilAsync();
        return perfil is null ? NotFound() : Ok(perfil);
    }

    [HttpGet("estado-cierre")]
    public async Task<IActionResult> EstadoCierre()
    {
        var negocio = await CargarMiNegocioAsync();
        if (negocio is null) return Forbid();

        if (negocio.CuentaCerradaAt is not { } cerrada || negocio.Activo)
        {
            return Ok(new EstadoCierreResponse(false, null));
        }

        var expira = cerrada.AddDays(GraciaDias);
        var dias = Math.Max(0, (int)Math.Ceiling((expira - Now()).TotalDays));
        return Ok(new EstadoCierreResponse(true, dias));
    }

    [HttpPost("cerrar-cuenta")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> CerrarCuenta()
    {
        var negocio = await CargarMiNegocioAsync();
        if (negocio is null) return Forbid();

        negocio.CuentaCerradaAt = Now();
        negocio.Activo = false;
        negocio.UpdatedAt = Now();
        await _db.SaveChangesAsync();

        _session.ClearCookies(Response);
        return Ok(new { ok = true });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SUPERADMIN
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> CrearConAdmin([FromBody] CrearNegocioRequest request)
    {
        try
        {
            var input = new CrearConAdminInput(request.Nombre, request.Slug, request.AdminEmail, request.AdminPassword, request.AdminNombre, request.Plan);
            var r = await _negocio.CrearConAdminAsync(input);
            return StatusCode(StatusCodes.Status201Created, r);
        }
        catch (NegocioException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpGet]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> ListarTodos()
    {
        var negocios = await _db.Negocios.IgnoreQueryFilters()
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.Id, n.Nombre, n.Slug, n.Activo, n.Plan, n.TrialExpira, n.CuentaCerradaAt, n.CreatedAt,
                Admin = n.Users
                    .Where(u => u.Role == Role.ADMIN)
                    .Select(u => new NegocioAdminResponse(u.Id, u.Nombre, u.Email))
                    .FirstOrDefault(),
            })
            .ToListAsync();

        var data = negocios.Select(n => new NegocioListItemResponse(
            n.Id, n.Nombre, n.Slug, n.Activo, n.Plan, n.TrialExpira, n.CuentaCerradaAt, n.CreatedAt,
            PlanEstado.Calcular(n.Activo, n.Plan, n.TrialExpira), n.Admin));

        return Ok(data);
    }

    [HttpGet("{id}")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> FindById(string id)
    {
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == id);
        return negocio is null ? NotFound(new { message = "Negocio no encontrado" }) : Ok(MapDetalle(negocio));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> Actualizar(string id, [FromBody] ActualizarNegocioRequest request)
    {
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == id);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug != negocio.Slug)
        {
            if (await _db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Slug == request.Slug && n.Id != id))
            {
                return Conflict(new { message = "El slug ya está en uso" });
            }
            negocio.Slug = request.Slug;
        }

        if (!string.IsNullOrWhiteSpace(request.Nombre)) negocio.Nombre = request.Nombre;
        if (request.Activo is { } activo) negocio.Activo = activo;
        if (!string.IsNullOrWhiteSpace(request.Plan)) negocio.Plan = request.Plan;
        negocio.UpdatedAt = Now();

        await _db.SaveChangesAsync();
        return Ok(MapDetalle(negocio));
    }

    [HttpPost("{id}/activar")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> ActivarPlan(string id)
    {
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == id);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        negocio.Plan = "activo";
        negocio.Activo = true;
        negocio.TrialExpira = null;
        negocio.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(MapDetalle(negocio));
    }

    [HttpPost("{id}/desactivar")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> DesactivarPlan(string id)
    {
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == id);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        negocio.Activo = false;
        negocio.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(MapDetalle(negocio));
    }

    [HttpPost("{id}/extender-trial")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> ExtenderTrial(string id, [FromBody] ExtenderTrialRequest request)
    {
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == id);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        var now = Now();
        var baseDate = negocio.TrialExpira is { } exp && exp > now ? exp : now;
        negocio.TrialExpira = baseDate.AddDays(request.Dias);
        negocio.Plan = "trial";
        negocio.Activo = true;
        negocio.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return Ok(MapDetalle(negocio));
    }

    [HttpPost("limpiar-cerradas")]
    [Authorize(Roles = "SUPERADMIN")]
    public async Task<IActionResult> LimpiarCerradas()
    {
        var eliminados = await _negocio.LimpiarCuentasCerradasAsync();
        return Ok(new LimpiezaResponse(eliminados));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private Task<Negocio?> CargarMiNegocioAsync()
    {
        var negocioId = _tenant.NegocioId;
        return string.IsNullOrEmpty(negocioId)
            ? Task.FromResult<Negocio?>(null)
            : _db.Negocios.FirstOrDefaultAsync(n => n.Id == negocioId);
    }

    private async Task<MiPerfilResponse?> ArmarMiPerfilAsync()
    {
        var negocio = await CargarMiNegocioAsync();
        var userId = ActorSub();
        if (negocio is null || string.IsNullOrEmpty(userId)) return null;

        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new UsuarioResponse(u.Id, u.Email, u.Nombre, u.Role.ToString()))
            .FirstOrDefaultAsync();
        if (user is null) return null;

        var estado = PlanEstado.Calcular(negocio.Activo, negocio.Plan, negocio.TrialExpira);
        var negocioDto = new MiPerfilNegocio(
            negocio.Id, negocio.Nombre, negocio.Slug, negocio.LogoUrl, negocio.Plan,
            negocio.TrialExpira, negocio.CuentaCerradaAt, estado,
            PlanEstado.DiasRestantesTrial(estado, negocio.TrialExpira));

        return new MiPerfilResponse(negocioDto, user);
    }

    private static NegocioDetalleResponse MapDetalle(Negocio n) => new(
        n.Id, n.Nombre, n.Slug, n.Activo, n.Plan, n.TrialExpira, n.CuentaCerradaAt, n.LogoUrl, n.CreatedAt, n.UpdatedAt);

    private string ActorSub() => User.FindFirst("sub")?.Value ?? string.Empty;

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
