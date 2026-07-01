using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Pedidos;
using OrbIT.Api.Contracts.Turnos;
using OrbIT.Application.Common;
using OrbIT.Application.Turnos;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Módulo de Turnos (caja del negocio). Apertura y cierre —transaccionales, con cálculo de ventas/efectivo—
/// viven en <see cref="ITurnoService"/>; el turno activo comparte ese cálculo. El historial y las stats son
/// solo lectura: historial paginado con projection, stats con GroupBy server-side.
///
/// <para><b>Divergencia deliberada respecto al NestJS: turno GLOBAL por negocio.</b> Hay un único turno
/// activo por negocio (no uno por empleado). Por eso <c>/activo</c> devuelve el turno del negocio (accesible a
/// ADMIN y TRABAJADOR) y el <c>/activo-global</c> del NestJS desaparece por redundante. Ver <see cref="ITurnoService"/>.</para>
/// </summary>
[ApiController]
[Route("turnos")]
[Authorize]
public sealed class TurnosController : ControllerBase
{
    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly ITurnoService _turnos;

    public TurnosController(OrbitDbContext db, ITenantProvider tenant, ITurnoService turnos)
    {
        _db = db;
        _tenant = tenant;
        _turnos = turnos;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Apertura / cierre (transaccional)
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("abrir")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Abrir([FromBody] AbrirTurnoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        try
        {
            var turno = await _turnos.AbrirTurnoAsync(ActorSub(), request.MontoInicial, request.Notas, negocioId);
            return Ok(MapTurno(turno));
        }
        catch (TurnoException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpPost("cerrar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Cerrar([FromBody] CerrarTurnoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        try
        {
            var result = await _turnos.CerrarTurnoAsync(ActorSub(), request.MontoFinal, request.Notas, negocioId);
            return Ok(new CierreTurnoResponse(MapTurno(result.Turno), result.MontoEsperado, result.Diferencia, result.AlertaDiferencia));
        }
        catch (TurnoException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpGet("activo")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Activo()
    {
        var activo = await _turnos.ObtenerTurnoActivoAsync(_tenant.NegocioId ?? string.Empty);
        if (activo is null)
        {
            // Sin turno abierto en el negocio → 204 (el NestJS devolvía 200 + null; 204 es el equivalente REST).
            return NoContent();
        }

        var t = activo.Turno;
        return Ok(new TurnoActivoResponse(
            t.Id, t.UserId, t.UserNombre, t.HoraInicio, t.CajaAperturaMonto,
            activo.VentasEnVivo, activo.MontoEsperado, t.Notas, t.CreatedAt));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Reporting (solo lectura)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Historial paginado (offset). Filtros: rango de fechas (AR -03:00 sobre <c>horaInicio</c>) y usuario.
    /// Projection liviana a DTO en vez de traer la entidad + user completo (mejora sobre el NestJS, que hacía
    /// findMany sin paginación).
    /// </summary>
    [HttpGet("historial")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Historial(
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] string? userId = null,
        [FromQuery] int? page = null,
        [FromQuery] int? limit = null)
    {
        var pageNum = page is > 0 ? page.Value : 1;
        var lim = Math.Clamp(limit ?? 50, 1, 200);

        var query = _db.Turnos.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(t => t.UserId == userId);
        if (DesdeArUtc(desde) is { } d) query = query.Where(t => t.HoraInicio >= d);
        if (HastaArUtc(hasta) is { } h) query = query.Where(t => t.HoraInicio <= h);

        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(t => t.HoraInicio)
            .Skip((pageNum - 1) * lim)
            .Take(lim)
            .Select(t => new TurnoRow(
                t.Id, t.UserId, t.User.Nombre, t.HoraInicio, t.HoraFin, t.CajaAperturaMonto,
                t.CajaCierreMonto, t.VentasTotales, t.MontoEsperado, t.Notas, t.CreatedAt))
            .ToListAsync();

        var data = rows.Select(MapRow).ToList();
        var totalPages = (int)Math.Ceiling(total / (double)lim);
        return Ok(new PaginatedResponse<TurnoResponse>(data, total, pageNum, totalPages));
    }

    /// <summary>
    /// Estadísticas del período sobre turnos cerrados. El agrupado por usuario (turnos, ventas y diferencias)
    /// se hace con GroupBy en Postgres (mejora sobre el NestJS, que traía todos los turnos y agrupaba con un
    /// Map en JS). La diferencia de cada turno usa <c>COALESCE(montoEsperado, apertura + ventas)</c> para los
    /// turnos legacy que no persistieron <c>montoEsperado</c>.
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Stats(
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] string? userId = null)
    {
        var cerrados = _db.Turnos.AsNoTracking().Where(t => t.HoraFin != null);
        if (!string.IsNullOrWhiteSpace(userId)) cerrados = cerrados.Where(t => t.UserId == userId);
        if (DesdeArUtc(desde) is { } d) cerrados = cerrados.Where(t => t.HoraInicio >= d);
        if (HastaArUtc(hasta) is { } h) cerrados = cerrados.Where(t => t.HoraInicio <= h);

        var porUsuarioRaw = await cerrados
            .GroupBy(t => t.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Turnos = g.Count(),
                VentasTotales = g.Sum(t => t.VentasTotales),
                Diferencias = g.Sum(t => (t.CajaCierreMonto ?? 0) - (t.MontoEsperado ?? (t.CajaAperturaMonto + t.VentasTotales))),
            })
            .ToListAsync();

        var conDiferenciaNegativa = await cerrados.CountAsync(
            t => (t.CajaCierreMonto ?? 0) - (t.MontoEsperado ?? (t.CajaAperturaMonto + t.VentasTotales)) < 0);

        var ids = porUsuarioRaw.Select(x => x.UserId).ToList();
        var nombres = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Nombre })
            .ToDictionaryAsync(u => u.Id, u => u.Nombre);

        var porUsuario = porUsuarioRaw
            .OrderByDescending(x => x.Turnos)
            .ThenByDescending(x => x.VentasTotales)
            .Select(x => new TurnoStatsUsuario(x.UserId, nombres.GetValueOrDefault(x.UserId), x.Turnos, x.VentasTotales, x.Diferencias))
            .ToList();

        var response = new TurnoStatsResponse(
            porUsuarioRaw.Sum(x => x.Turnos),
            conDiferenciaNegativa,
            porUsuarioRaw.Sum(x => x.Diferencias),
            porUsuarioRaw.Sum(x => x.VentasTotales),
            porUsuario);

        return Ok(response);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private sealed record TurnoRow(
        string Id, string UserId, string? Nombre, DateTime HoraInicio, DateTime? HoraFin,
        double CajaAperturaMonto, double? CajaCierreMonto, double VentasTotales,
        double? MontoEsperado, string? Notas, DateTime CreatedAt);

    private static TurnoResponse MapTurno(TurnoDto t)
    {
        var (esperado, diferencia, alerta) = DiferenciaDe(t.CajaAperturaMonto, t.VentasTotales, t.MontoEsperado, t.CajaCierreMonto);
        return new TurnoResponse(
            t.Id, t.UserId, t.UserNombre, t.HoraInicio, t.HoraFin, t.CajaAperturaMonto,
            t.CajaCierreMonto, t.VentasTotales, esperado, diferencia, alerta, t.Notas, t.CreatedAt);
    }

    private static TurnoResponse MapRow(TurnoRow r)
    {
        var (esperado, diferencia, alerta) = DiferenciaDe(r.CajaAperturaMonto, r.VentasTotales, r.MontoEsperado, r.CajaCierreMonto);
        return new TurnoResponse(
            r.Id, r.UserId, r.Nombre, r.HoraInicio, r.HoraFin, r.CajaAperturaMonto,
            r.CajaCierreMonto, r.VentasTotales, esperado, diferencia, alerta, r.Notas, r.CreatedAt);
    }

    /// <summary>
    /// Esperado (efectivo), diferencia y alerta de un turno. Turnos nuevos persisten <c>montoEsperado</c>; los
    /// antiguos caen al cálculo legacy (apertura + ventasTotales). La diferencia es null si el turno sigue
    /// abierto (sin <c>cajaCierreMonto</c>).
    /// </summary>
    private static (double? Esperado, double? Diferencia, bool Alerta) DiferenciaDe(
        double apertura, double ventasTotales, double? montoEsperado, double? cajaCierreMonto)
    {
        var esperado = montoEsperado ?? (apertura + ventasTotales);
        if (cajaCierreMonto is not { } cierre)
        {
            return (esperado, null, false);
        }
        var diferencia = cierre - esperado;
        return (esperado, diferencia, diferencia < -100);
    }

    private string ActorSub() => User.FindFirst("sub")?.Value ?? string.Empty;

    /// <summary>Inicio del día AR (00:00 -03:00) como instante UTC comparable contra <c>horaInicio</c>.</summary>
    private static DateTime? DesdeArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date) : null;

    /// <summary>Fin del día AR (23:59:59.999… -03:00) como instante UTC comparable contra <c>horaInicio</c>.</summary>
    private static DateTime? HastaArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date.AddDays(1).AddTicks(-1)) : null;

    private static bool TryParseDate(string? value, out DateTime fecha)
    {
        fecha = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }
}
