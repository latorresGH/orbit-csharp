using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Proveedores;
using OrbIT.Api.MultiTenancy;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de proveedores, scopeado por negocio (tenant) vía los Global Query Filters del
/// <c>OrbitDbContext</c>. Módulo simple del catálogo: sin endpoints públicos, escritura ADMIN-only,
/// lectura ADMIN/TRABAJADOR.
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Duplicado de nombre:</b> NestJS NO pre-chequeaba al crear y reventaría el índice único
///   (<c>Proveedor_nombre_negocioId_key</c>) con un 500. Acá se pre-chequea → 409 Conflict, con backstop
///   por <c>23505</c> para el caso de carrera (consistencia con el resto del proyecto).</item>
///   <item><b>Borrado con insumos asignados:</b> la FK <c>Insumo.proveedorId</c> es <c>ON DELETE SET NULL</c>,
///   así que la base NO bloquea el borrado. El guard es 100% aplicativo: si el proveedor tiene insumos
///   se rechaza con 400 (paridad funcional con NestJS) — no hay backstop de DB posible.</item>
///   <item><b>Tenant del detalle:</b> el chequeo de pertenencia al negocio es estructural (query filter →
///   id ajeno = null = 404), sin comparar <c>negocioId</c> a mano.</item>
/// </list>
/// </summary>
[ApiController]
[Route("proveedores")]
[Authorize]
public sealed class ProveedorController : ControllerBase
{
    // Código SQLSTATE de violación de unicidad en PostgreSQL (backstop del pre-chequeo de nombre).
    private const string UniqueViolation = "23505";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public ProveedorController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetAll([FromQuery] bool incluirInactivos = false)
    {
        var query = _db.Proveedors.AsNoTracking().AsQueryable();
        if (!incluirInactivos)
        {
            query = query.Where(p => p.Activo);
        }

        var proveedores = await query
            .OrderBy(p => p.Nombre)
            .Select(p => new ProveedorResponse(
                p.Id, p.Nombre, p.Telefono, p.Email, p.Notas, p.Activo, p.CreatedAt, p.UpdatedAt))
            .ToListAsync();
        return Ok(proveedores);
    }

    [HttpGet("{id}", Name = nameof(GetProveedorById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetProveedorById(string id)
    {
        // El query filter garantiza la pertenencia al tenant (id ajeno → null → 404).
        var proveedor = await _db.Proveedors.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProveedorDetalleResponse(
                p.Id, p.Nombre, p.Telefono, p.Email, p.Notas, p.Activo, p.CreatedAt, p.UpdatedAt,
                p.Insumos
                    .OrderBy(i => i.Nombre)
                    .Select(i => new ProveedorInsumoResponse(i.Id, i.Nombre, i.StockActual, i.Activo, i.UnidadMedida))
                    .ToList()))
            .FirstOrDefaultAsync();
        return proveedor is null ? NotFound() : Ok(proveedor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateProveedorRequest request)
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
        if (await _db.Proveedors.AnyAsync(p => p.Nombre == nombre))
        {
            return NombreDuplicado(nombre);
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var proveedor = new Proveedor
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            Telefono = TrimOrNull(request.Telefono),
            Email = TrimOrNull(request.Email),
            Notas = TrimOrNull(request.Notas),
            Activo = true,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Proveedors.Add(proveedor);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(nombre);
        }

        return CreatedAtAction(nameof(GetProveedorById), new { id = proveedor.Id }, ToResponse(proveedor));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateProveedorRequest request)
    {
        var proveedor = await _db.Proveedors.FirstOrDefaultAsync(p => p.Id == id);
        if (proveedor is null)
        {
            return NotFound();
        }

        if (request.Nombre is not null)
        {
            var nombre = request.Nombre.Trim();
            if (!string.Equals(proveedor.Nombre, nombre, StringComparison.Ordinal)
                && await _db.Proveedors.AnyAsync(p => p.Nombre == nombre && p.Id != id))
            {
                return NombreDuplicado(nombre);
            }
            proveedor.Nombre = nombre;
        }

        if (request.Telefono is not null) proveedor.Telefono = TrimOrNull(request.Telefono);
        if (request.Email is not null) proveedor.Email = TrimOrNull(request.Email);
        if (request.Notas is not null) proveedor.Notas = TrimOrNull(request.Notas);
        if (request.Activo is { } activo) proveedor.Activo = activo;

        proveedor.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(request.Nombre!.Trim());
        }

        return Ok(ToResponse(proveedor));
    }

    [HttpPatch("{id}/activo")]
    [Authorize(Roles = "ADMIN")]
    public Task<IActionResult> SetActivo(string id, [FromBody] SetProveedorActivoRequest request) =>
        CambiarActivo(id, request.Activo);

    [HttpPatch("{id}/baja")]
    [Authorize(Roles = "ADMIN")]
    public Task<IActionResult> Baja(string id) => CambiarActivo(id, false);

    [HttpPatch("{id}/alta")]
    [Authorize(Roles = "ADMIN")]
    public Task<IActionResult> Alta(string id) => CambiarActivo(id, true);

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var proveedor = await _db.Proveedors.FirstOrDefaultAsync(p => p.Id == id);
        if (proveedor is null)
        {
            return NotFound();
        }

        // La FK Insumo.proveedorId es ON DELETE SET NULL: la base no bloquea el borrado, así que el
        // guard es puramente aplicativo (no hay backstop de DB posible). Paridad funcional con NestJS.
        if (await _db.Insumos.AnyAsync(i => i.ProveedorId == id))
        {
            return BadRequest(new
            {
                message = "No se puede borrar un proveedor con insumos asignados. " +
                          "Usá baja lógica (activo=false) o desasigná los insumos.",
            });
        }

        _db.Proveedors.Remove(proveedor);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> CambiarActivo(string id, bool activo)
    {
        var proveedor = await _db.Proveedors.FirstOrDefaultAsync(p => p.Id == id);
        if (proveedor is null)
        {
            return NotFound();
        }

        proveedor.Activo = activo;
        proveedor.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        await _db.SaveChangesAsync();
        return Ok(ToResponse(proveedor));
    }

    private static string? TrimOrNull(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static ProveedorResponse ToResponse(Proveedor p) =>
        new(p.Id, p.Nombre, p.Telefono, p.Email, p.Notas, p.Activo, p.CreatedAt, p.UpdatedAt);

    private ConflictObjectResult NombreDuplicado(string nombre) =>
        Conflict(new { message = $"Ya existe un proveedor con el nombre '{nombre}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
