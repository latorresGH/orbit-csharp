using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Billing;
using OrbIT.Api.Contracts.Usuarios;
using OrbIT.Application.Auth;
using OrbIT.Application.Planes;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Gestión de empleados del negocio (TRABAJADOR / DELIVERY) por parte del ADMIN. Scopeado por tenant vía los
/// Global Query Filters del <c>OrbitDbContext</c> (el filtro de <c>User</c> es por <c>NegocioId</c>), así que
/// las lecturas y el pre-check de email ya quedan acotados al negocio del ADMIN sin filtrar a mano.
///
/// El alta pasa por <see cref="IPlanGuard"/> (límite de usuarios del plan: Básico 5 / Pro 12). El SUPERADMIN
/// y el onboarding (registro/verificación del ADMIN) NO pasan por acá — este módulo es sólo para que un ADMIN
/// gestione a su equipo. No se puede crear ni ascender a ADMIN/SUPERADMIN por esta vía.
/// </summary>
[ApiController]
[Route("usuarios")]
[Authorize(Roles = "ADMIN")]
public sealed class UsuariosController : ControllerBase
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IPasswordHasher _hasher;
    private readonly IPlanGuard _planGuard;

    public UsuariosController(OrbitDbContext db, ITenantProvider tenant, IPasswordHasher hasher, IPlanGuard planGuard)
    {
        _db = db;
        _tenant = tenant;
        _hasher = hasher;
        _planGuard = planGuard;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var usuarios = await _db.Users.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new UsuarioListItemResponse(
                u.Id, u.Email, u.Nombre, u.Role.ToString(), u.Activo, u.CreatedAt))
            .ToListAsync();
        return Ok(usuarios);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearUsuarioRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        // Sólo empleados: no se crea otro ADMIN ni un SUPERADMIN por esta vía.
        if (request.Rol is not (Role.TRABAJADOR or Role.DELIVERY))
        {
            return BadRequest(new { message = "El rol debe ser TRABAJADOR o DELIVERY." });
        }

        // Plan: límite de usuarios del negocio (Básico 5 / Pro 12).
        var limite = await _planGuard.VerificarLimiteUsuariosAsync(negocioId);
        if (!limite.Permitido)
        {
            return PlanGuardResponses.LimiteUsuarios(limite.Limite);
        }

        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length == 0)
        {
            return BadRequest(new { message = "El email es obligatorio." });
        }
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest(new { message = "La contraseña debe tener al menos 6 caracteres." });
        }
        var nombre = (request.Nombre ?? string.Empty).Trim();
        if (nombre.Length == 0)
        {
            return BadRequest(new { message = "El nombre es obligatorio." });
        }

        // Pre-check de duplicado (índice único email+negocioId; el GQF ya acota al negocio) → 409 antes de reventar.
        if (await _db.Users.AnyAsync(u => u.Email == email))
        {
            return EmailDuplicado(email);
        }

        var usuario = new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            Password = _hasher.Hash(request.Password),
            Nombre = nombre,
            Role = request.Rol,
            Activo = true,
            EmailVerificado = true, // lo crea el ADMIN: no requiere el flujo de código de verificación.
            NegocioId = negocioId,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
        };
        _db.Users.Add(usuario);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return EmailDuplicado(email);
        }

        var response = new UsuarioListItemResponse(
            usuario.Id, usuario.Email, usuario.Nombre, usuario.Role.ToString(), usuario.Activo, usuario.CreatedAt);
        return CreatedAtAction(nameof(GetAll), new { id = usuario.Id }, response);
    }

    [HttpPatch("{id}/activo")]
    public async Task<IActionResult> SetActivo(string id, [FromBody] ToggleUsuarioActivoRequest request)
    {
        var usuario = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (usuario is null)
        {
            return NotFound(new { message = "Usuario no encontrado" });
        }

        // No permitir que el ADMIN se autodesactive ni deje al negocio sin ningún ADMIN activo.
        if (!request.Activo && usuario.Role == Role.ADMIN)
        {
            if (usuario.Id == ActorSub())
            {
                return BadRequest(new { message = "No podés desactivar tu propia cuenta." });
            }
            if (!await OtroAdminActivoExiste(id))
            {
                return BadRequest(new { message = "No podés desactivar al único ADMIN activo del negocio." });
            }
        }

        usuario.Activo = request.Activo;
        await _db.SaveChangesAsync();

        return Ok(new UsuarioListItemResponse(
            usuario.Id, usuario.Email, usuario.Nombre, usuario.Role.ToString(), usuario.Activo, usuario.CreatedAt));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var usuario = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (usuario is null)
        {
            return NotFound(new { message = "Usuario no encontrado" });
        }

        if (usuario.Id == ActorSub())
        {
            return BadRequest(new { message = "No podés borrar tu propia cuenta." });
        }
        // No dejar al negocio sin ningún ADMIN.
        if (usuario.Role == Role.ADMIN && !await OtroAdminExiste(id))
        {
            return BadRequest(new { message = "No podés borrar al último ADMIN del negocio." });
        }

        _db.Users.Remove(usuario);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            // El usuario ya tiene historial (pedidos/turnos/caja): no se puede borrar físicamente.
            return Conflict(new
            {
                message = "No se puede borrar un usuario con historial. Desactivalo (activo=false) en su lugar.",
            });
        }

        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private Task<bool> OtroAdminExiste(string excluyendoId) =>
        _db.Users.AnyAsync(u => u.Role == Role.ADMIN && u.Id != excluyendoId);

    private Task<bool> OtroAdminActivoExiste(string excluyendoId) =>
        _db.Users.AnyAsync(u => u.Role == Role.ADMIN && u.Activo && u.Id != excluyendoId);

    private string ActorSub() => User.FindFirst("sub")?.Value ?? string.Empty;

    private ConflictObjectResult EmailDuplicado(string email) =>
        Conflict(new { message = $"Ya existe un usuario con el email '{email}' en este negocio." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ForeignKeyViolation };
}
