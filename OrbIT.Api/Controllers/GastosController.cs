using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Gastos;
using OrbIT.Application.Common;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de gastos operativos, scopeado por negocio (tenant) vía Global Query Filters. Todo ADMIN-only,
/// paridad con el NestJS. El listado ya viene paginado (offset, <c>page</c> 0-indexado como el original) y el
/// resumen agrega con GroupBy server-side en Postgres. La categoría se valida contra una lista fija (no hay
/// enum de DB). Filtros de fecha unificados a AR (-03:00), consistente con el resto del reporting.
/// </summary>
[ApiController]
[Route("gastos")]
[Authorize(Roles = "ADMIN")]
public sealed class GastosController : ControllerBase
{
    private static readonly HashSet<string> CategoriasValidas = new(StringComparer.Ordinal)
    {
        "GAS", "LUZ", "AGUA", "ALQUILER", "INSUMOS_EXTRA", "MANTENIMIENTO", "SALARIOS", "OTROS",
    };

    private static readonly string CategoriasTexto = string.Join(", ", CategoriasValidas);

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public GastosController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CreateGastoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!CategoriasValidas.Contains(request.Categoria))
        {
            return BadRequest(new { message = $"Categoría inválida. Válidas: {CategoriasTexto}" });
        }

        var now = Now();
        var gasto = new GastoOperativo
        {
            Id = Guid.NewGuid().ToString(),
            Categoria = request.Categoria,
            Monto = request.Monto,
            Descripcion = TrimOrNull(request.Descripcion),
            Fecha = ParseFecha(request.Fecha) ?? now,
            ComprobanteUrl = TrimOrNull(request.ComprobanteUrl),
            CreadoPor = TrimOrNull(request.CreadoPor),
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.GastoOperativos.Add(gasto);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Obtener), new { id = gasto.Id }, ToResponse(gasto));
    }

    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? categoria = null,
        [FromQuery] int page = 0,
        [FromQuery] int limit = 10)
    {
        var pageNum = Math.Max(page, 0);
        var lim = Math.Clamp(limit, 1, 200);

        var query = FiltrarPorFecha(_db.GastoOperativos.AsNoTracking(), fechaInicio, fechaFin);
        if (!string.IsNullOrWhiteSpace(categoria)) query = query.Where(g => g.Categoria == categoria);

        var total = await query.CountAsync();
        var data = await query
            .OrderByDescending(g => g.Fecha)
            .Skip(pageNum * lim)
            .Take(lim)
            .Select(g => new GastoResponse(
                g.Id, g.Categoria, g.Monto, g.Descripcion, g.Fecha, g.ComprobanteUrl, g.CreadoPor, g.CreatedAt, g.UpdatedAt))
            .ToListAsync();

        return Ok(new GastosPagedResponse(data, total));
    }

    /// <summary>Resumen del período: total + cantidad + desglose por categoría (GroupBy server-side, orden por total desc).</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null)
    {
        var query = FiltrarPorFecha(_db.GastoOperativos.AsNoTracking(), fechaInicio, fechaFin);

        // GroupBy server-side (proyección a tipo anónimo); el orden por total desc y los totales globales se
        // resuelven en memoria sobre las ≤8 categorías (evita ordenar por un agregado proyectado en SQL).
        var porCategoriaRaw = await query
            .GroupBy(g => g.Categoria)
            .Select(grp => new { Categoria = grp.Key, Total = grp.Sum(g => g.Monto), Cantidad = grp.Count() })
            .ToListAsync();

        var porCategoria = porCategoriaRaw
            .OrderByDescending(x => x.Total)
            .Select(x => new GastoResumenCategoria(x.Categoria, x.Total, x.Cantidad))
            .ToList();

        var total = porCategoriaRaw.Sum(x => x.Total);
        var cantidad = porCategoriaRaw.Sum(x => x.Cantidad);

        return Ok(new GastoResumenResponse(total, cantidad, porCategoria));
    }

    [HttpGet("{id}", Name = nameof(Obtener))]
    public async Task<IActionResult> Obtener(string id)
    {
        var gasto = await _db.GastoOperativos.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new GastoResponse(
                g.Id, g.Categoria, g.Monto, g.Descripcion, g.Fecha, g.ComprobanteUrl, g.CreadoPor, g.CreatedAt, g.UpdatedAt))
            .FirstOrDefaultAsync();
        return gasto is null ? NotFound(new { message = "Gasto no encontrado" }) : Ok(gasto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Actualizar(string id, [FromBody] UpdateGastoRequest request)
    {
        var gasto = await _db.GastoOperativos.FirstOrDefaultAsync(g => g.Id == id);
        if (gasto is null)
        {
            return NotFound(new { message = "Gasto no encontrado" });
        }
        if (request.Categoria is not null && !CategoriasValidas.Contains(request.Categoria))
        {
            return BadRequest(new { message = $"Categoría inválida. Válidas: {CategoriasTexto}" });
        }

        if (request.Categoria is not null) gasto.Categoria = request.Categoria;
        if (request.Monto is { } monto) gasto.Monto = monto;
        if (request.Descripcion is not null) gasto.Descripcion = TrimOrNull(request.Descripcion);
        if (request.Fecha is not null && ParseFecha(request.Fecha) is { } fecha) gasto.Fecha = fecha;
        if (request.ComprobanteUrl is not null) gasto.ComprobanteUrl = TrimOrNull(request.ComprobanteUrl);
        gasto.UpdatedAt = Now();

        await _db.SaveChangesAsync();
        return Ok(ToResponse(gasto));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Eliminar(string id)
    {
        var gasto = await _db.GastoOperativos.FirstOrDefaultAsync(g => g.Id == id);
        if (gasto is null)
        {
            return NotFound(new { message = "Gasto no encontrado" });
        }

        _db.GastoOperativos.Remove(gasto);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static IQueryable<GastoOperativo> FiltrarPorFecha(IQueryable<GastoOperativo> query, string? desde, string? hasta)
    {
        if (DesdeArUtc(desde) is { } d) query = query.Where(g => g.Fecha >= d);
        if (HastaArUtc(hasta) is { } h) query = query.Where(g => g.Fecha <= h);
        return query;
    }

    private static GastoResponse ToResponse(GastoOperativo g) => new(
        g.Id, g.Categoria, g.Monto, g.Descripcion, g.Fecha, g.ComprobanteUrl, g.CreadoPor, g.CreatedAt, g.UpdatedAt);

    private static string? TrimOrNull(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Parsea una fecha ISO a <c>Kind=Unspecified</c> (columna timestamp without time zone). Null si no parsea.</summary>
    private static DateTime? ParseFecha(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified)
            : null;

    private static DateTime? DesdeArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date) : null;

    private static DateTime? HastaArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date.AddDays(1).AddTicks(-1)) : null;

    private static bool TryParseDate(string? value, out DateTime fecha)
    {
        fecha = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
