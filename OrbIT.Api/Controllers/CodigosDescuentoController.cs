using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Billing;
using OrbIT.Api.Contracts.CodigosDescuento;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.CodigosDescuento;
using OrbIT.Application.Planes;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de códigos de descuento + validación pública, scopeado por negocio (tenant). La validación vive en
/// <see cref="ICodigosDescuentoService"/> (OrbIT.Application), reutilizable por el futuro PedidosController.
///
/// Roles: escritura ADMIN-only; lectura ADMIN/TRABAJADOR; <see cref="Validar"/> es público con tenant por
/// <c>?negocio=slug</c> (NestJS lo resolvía siempre por slug, encaja directo en
/// <see cref="AllowAnonymousWithTenantAttribute"/>).
///
/// Paridad y mejoras: duplicado de código → <b>409</b> (NestJS devolvía 400) con backstop <c>23505</c>;
/// <c>productoId</c> se valida estructuralmente contra el tenant (NestJS no lo hacía).
/// </summary>
[ApiController]
[Route("codigos-descuento")]
[Authorize]
public sealed class CodigosDescuentoController : ControllerBase
{
    private const string UniqueViolation = "23505";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly ICodigosDescuentoService _service;
    private readonly IPlanGuard _planGuard;

    public CodigosDescuentoController(OrbitDbContext db, ITenantProvider tenant, ICodigosDescuentoService service, IPlanGuard planGuard)
    {
        _db = db;
        _tenant = tenant;
        _service = service;
        _planGuard = planGuard;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Validación pública (menú): tenant por claim o ?negocio=slug.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost("validar")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> Validar([FromBody] ValidarCodigoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        // Devuelve 200 con { valido:false, error } cuando no aplica (es un resultado, no un error HTTP).
        var resultado = await _service.ValidarAsync(request.Codigo, negocioId, TrimOrNull(request.ProductoId));
        return Ok(resultado);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetAll()
    {
        var codigos = await _db.CodigoDescuentos.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(CodigoProjection)
            .ToListAsync();
        return Ok(codigos);
    }

    [HttpGet("{id}", Name = nameof(GetCodigoById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetCodigoById(string id)
    {
        var codigo = await _db.CodigoDescuentos.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(CodigoProjection)
            .FirstOrDefaultAsync();
        return codigo is null ? NotFound() : Ok(codigo);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Crear([FromBody] CreateCodigoDescuentoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!await _planGuard.VerificarFeatureAsync(negocioId, PlanFeature.Ofertas))
        {
            return PlanGuardResponses.Feature();
        }

        if (!TryParseFecha(request.FechaInicio, out var fechaInicio))
        {
            return BadRequest(new { message = "fechaInicio inválida." });
        }
        if (!TryParseFecha(request.FechaFin, out var fechaFin))
        {
            return BadRequest(new { message = "fechaFin inválida." });
        }
        if (fechaFin <= fechaInicio)
        {
            return BadRequest(new { message = "La fecha de fin debe ser posterior a la fecha de inicio." });
        }
        if (request.TipoDescuento == "PORCENTAJE" && request.Valor is <= 0 or > 100)
        {
            return BadRequest(new { message = "El porcentaje debe estar entre 0 y 100." });
        }

        var productoId = TrimOrNull(request.ProductoId);
        if (productoId is not null && !await _db.Productos.AnyAsync(p => p.Id == productoId))
        {
            return BadRequest(new { message = "El producto no existe o no pertenece a este negocio." });
        }

        var codigoNorm = request.Codigo.ToUpperInvariant().Trim();
        if (await _db.CodigoDescuentos.AnyAsync(c => c.Codigo == codigoNorm))
        {
            return CodigoDuplicado(codigoNorm);
        }

        var codigo = new CodigoDescuento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            Codigo = codigoNorm,
            Descripcion = TrimOrNull(request.Descripcion),
            TipoDescuento = request.TipoDescuento,
            Valor = request.Valor,
            ProductoId = productoId,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin,
            Activo = request.Activo ?? true,
            UsosMaximos = request.UsosMaximos,
            CreatedAt = Now(),
        };
        _db.CodigoDescuentos.Add(codigo);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return CodigoDuplicado(codigoNorm);
        }

        var response = await _db.CodigoDescuentos.AsNoTracking().Where(c => c.Id == codigo.Id).Select(CodigoProjection).FirstAsync();
        return CreatedAtAction(nameof(GetCodigoById), new { id = codigo.Id }, response);
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateCodigoDescuentoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!await _planGuard.VerificarFeatureAsync(negocioId, PlanFeature.Ofertas))
        {
            return PlanGuardResponses.Feature();
        }

        var codigo = await _db.CodigoDescuentos.FirstOrDefaultAsync(c => c.Id == id);
        if (codigo is null)
        {
            return NotFound();
        }

        var fechaInicio = codigo.FechaInicio;
        if (request.FechaInicio is not null)
        {
            if (!TryParseFecha(request.FechaInicio, out fechaInicio))
            {
                return BadRequest(new { message = "fechaInicio inválida." });
            }
        }
        var fechaFin = codigo.FechaFin;
        if (request.FechaFin is not null)
        {
            if (!TryParseFecha(request.FechaFin, out fechaFin))
            {
                return BadRequest(new { message = "fechaFin inválida." });
            }
        }
        if (fechaFin <= fechaInicio)
        {
            return BadRequest(new { message = "La fecha de fin debe ser posterior a la fecha de inicio." });
        }

        var tipoFinal = request.TipoDescuento ?? codigo.TipoDescuento;
        var valorFinal = request.Valor ?? codigo.Valor;
        if (tipoFinal == "PORCENTAJE" && valorFinal is <= 0 or > 100)
        {
            return BadRequest(new { message = "El porcentaje debe estar entre 0 y 100." });
        }

        if (request.ProductoId is not null)
        {
            var productoId = TrimOrNull(request.ProductoId);
            if (productoId is not null && !await _db.Productos.AnyAsync(p => p.Id == productoId))
            {
                return BadRequest(new { message = "El producto no existe o no pertenece a este negocio." });
            }
            codigo.ProductoId = productoId;
        }

        if (request.Codigo is not null)
        {
            var nuevoCodigo = request.Codigo.ToUpperInvariant().Trim();
            if (!string.Equals(codigo.Codigo, nuevoCodigo, StringComparison.Ordinal)
                && await _db.CodigoDescuentos.AnyAsync(c => c.Codigo == nuevoCodigo && c.Id != id))
            {
                return CodigoDuplicado(nuevoCodigo);
            }
            codigo.Codigo = nuevoCodigo;
        }

        if (request.Descripcion is not null) codigo.Descripcion = TrimOrNull(request.Descripcion);
        if (request.TipoDescuento is not null) codigo.TipoDescuento = request.TipoDescuento;
        if (request.Valor is { } valor) codigo.Valor = valor;
        codigo.FechaInicio = fechaInicio;
        codigo.FechaFin = fechaFin;
        if (request.Activo is { } activo) codigo.Activo = activo;
        if (request.UsosMaximos is { } usos) codigo.UsosMaximos = usos;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return CodigoDuplicado(codigo.Codigo);
        }

        var response = await _db.CodigoDescuentos.AsNoTracking().Where(c => c.Id == id).Select(CodigoProjection).FirstAsync();
        return Ok(response);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var codigo = await _db.CodigoDescuentos.FirstOrDefaultAsync(c => c.Id == id);
        if (codigo is null)
        {
            return NotFound();
        }

        // Sin dependientes con FK Restrict → borrado directo.
        _db.CodigoDescuentos.Remove(codigo);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private static bool TryParseFecha(string value, out DateTime fecha)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
        {
            fecha = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
            return true;
        }
        fecha = default;
        return false;
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static string? TrimOrNull(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private ConflictObjectResult CodigoDuplicado(string codigo) =>
        Conflict(new { message = $"Ya existe un código '{codigo}' para este negocio." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static readonly System.Linq.Expressions.Expression<Func<CodigoDescuento, CodigoDescuentoResponse>> CodigoProjection = c => new CodigoDescuentoResponse(
        c.Id, c.Codigo, c.Descripcion, c.TipoDescuento, c.Valor, c.ProductoId,
        c.Producto == null ? null : new CodigoProductoResponse(c.Producto.Id, c.Producto.Nombre),
        c.FechaInicio, c.FechaFin, c.Activo, c.UsosMaximos, c.UsosActuales, c.CreatedAt);
}
