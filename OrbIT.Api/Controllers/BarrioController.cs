using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Barrios;
using OrbIT.Api.MultiTenancy;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de barrios (zonas de envío), scopeado por negocio (tenant) vía los Global Query Filters del
/// <c>OrbitDbContext</c>. Los listados/detalle son parte del menú público (el storefront necesita las
/// zonas y sus precios de envío sin login).
///
/// Roles: escritura ADMIN-only; las lecturas (<see cref="GetAll"/> y <see cref="GetById"/>) son públicas
/// con resolución de tenant por <c>?negocio=slug</c> (<see cref="AllowAnonymousWithTenantAttribute"/>).
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Duplicado de nombre:</b> 409 Conflict (consistencia con el resto del proyecto), no el 400
///   que devolvía NestJS. Pre-chequeo + backstop por <c>23505</c>.</item>
///   <item><b>GET /barrios/{id}:</b> en NestJS era <c>@Public()</c> SIN scoping de tenant — un leak
///   cross-tenant (cualquiera leía un barrio de otro negocio por id). Acá queda estructuralmente cerrado:
///   <c>[AllowAnonymousWithTenant]</c> + query filter exigen y aplican el tenant (por claim o por slug).</item>
/// </list>
/// </summary>
[ApiController]
[Route("barrios")]
[Authorize]
public sealed class BarrioController : ControllerBase
{
    private const string UniqueViolation = "23505";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public BarrioController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura pública (menú): tenant por claim (si hay sesión) o por ?negocio=slug.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetAll([FromQuery] bool? activo = null)
    {
        var query = _db.Barrios.AsNoTracking().AsQueryable();
        if (activo is { } filtro)
        {
            query = query.Where(b => b.Activo == filtro);
        }

        var barrios = await query
            .OrderBy(b => b.Nombre)
            .Select(b => new BarrioResponse(b.Id, b.Nombre, b.PrecioEnvio, b.Activo, b.CreatedAt, b.UpdatedAt))
            .ToListAsync();
        return Ok(barrios);
    }

    [HttpGet("{id}", Name = nameof(GetById))]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetById(string id)
    {
        // El query filter aplica el tenant resuelto (claim o slug): un id de otro negocio → 404.
        var barrio = await _db.Barrios.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new BarrioResponse(b.Id, b.Nombre, b.PrecioEnvio, b.Activo, b.CreatedAt, b.UpdatedAt))
            .FirstOrDefaultAsync();
        return barrio is null ? NotFound(new { message = "Barrio no encontrado" }) : Ok(barrio);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateBarrioRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var nombre = request.Nombre.Trim();
        if (nombre.Length == 0)
        {
            return BadRequest(new { message = "El nombre es obligatorio." });
        }
        if (await _db.Barrios.AnyAsync(b => b.Nombre == nombre))
        {
            return NombreDuplicado(nombre);
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var barrio = new Barrio
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            PrecioEnvio = request.PrecioEnvio,
            Activo = request.Activo ?? true,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Barrios.Add(barrio);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(nombre);
        }

        return CreatedAtAction(nameof(GetById), new { id = barrio.Id }, ToResponse(barrio));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateBarrioRequest request)
    {
        var barrio = await _db.Barrios.FirstOrDefaultAsync(b => b.Id == id);
        if (barrio is null)
        {
            return NotFound(new { message = "Barrio no encontrado" });
        }

        if (request.Nombre is not null)
        {
            var nombre = request.Nombre.Trim();
            if (!string.Equals(barrio.Nombre, nombre, StringComparison.Ordinal)
                && await _db.Barrios.AnyAsync(b => b.Nombre == nombre && b.Id != id))
            {
                return NombreDuplicado(nombre);
            }
            barrio.Nombre = nombre;
        }

        if (request.PrecioEnvio is { } precio) barrio.PrecioEnvio = precio;
        if (request.Activo is { } activo) barrio.Activo = activo;

        barrio.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(request.Nombre!.Trim());
        }

        return Ok(ToResponse(barrio));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var barrio = await _db.Barrios.FirstOrDefaultAsync(b => b.Id == id);
        if (barrio is null)
        {
            return NotFound(new { message = "Barrio no encontrado" });
        }

        // Ninguna entidad referencia al barrio por FK: el borrado es directo, sin dependientes.
        _db.Barrios.Remove(barrio);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private static BarrioResponse ToResponse(Barrio b) =>
        new(b.Id, b.Nombre, b.PrecioEnvio, b.Activo, b.CreatedAt, b.UpdatedAt);

    private ConflictObjectResult NombreDuplicado(string nombre) =>
        Conflict(new { message = $"Ya existe un barrio con el nombre '{nombre}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
