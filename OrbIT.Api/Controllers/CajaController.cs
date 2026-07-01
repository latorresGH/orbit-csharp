using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Caja;
using OrbIT.Application.Caja;
using OrbIT.Application.Common;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Módulo de Caja. El registro de pago de un pedido y el batch —transaccionales— viven en
/// <see cref="ICajaService"/>; los movimientos manuales, la anulación (soft-delete) y las lecturas
/// (resumen agregado + lista paginada, pendientes de cobro, cuentas abiertas) viven acá. Todo va por los
/// Global Query Filters (tenant estructural por claim).
/// </summary>
[ApiController]
[Route("caja")]
[Authorize(Roles = "ADMIN,TRABAJADOR")]
public sealed class CajaController : ControllerBase
{
    private static readonly TipoMovimientoCaja[] TiposManualesValidos =
    {
        TipoMovimientoCaja.ENTRADA, TipoMovimientoCaja.SALIDA, TipoMovimientoCaja.AJUSTE,
    };

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly ICajaService _caja;

    public CajaController(OrbitDbContext db, ITenantProvider tenant, ICajaService caja)
    {
        _db = db;
        _tenant = tenant;
        _caja = caja;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Pagos (transaccional)
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("pedido/{pedidoId}/confirmar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ConfirmarPago(string pedidoId, [FromBody] ConfirmarPagoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        try
        {
            var mov = await _caja.RegistrarPagoPedidoAsync(pedidoId, ActorSub(), negocioId, request.GananciaRepartidor, request.MetodoPago);
            return StatusCode(StatusCodes.Status201Created, MapMovimiento(mov));
        }
        catch (CajaException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpPost("confirmar-todos")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ConfirmarTodos([FromBody] ConfirmarTodosRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var resultados = await _caja.ConfirmarPagosPendientesAsync(request.PedidoIds, ActorSub(), negocioId);
        return Ok(resultados);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Movimientos manuales
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("movimiento")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> RegistrarMovimiento([FromBody] MovimientoManualRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!TiposManualesValidos.Contains(request.Tipo))
        {
            return BadRequest(new { message = "Tipo de movimiento inválido (solo ENTRADA, SALIDA o AJUSTE)" });
        }
        if (!(request.Monto > 0))
        {
            return BadRequest(new { message = "El monto del movimiento debe ser mayor a 0" });
        }

        var now = Now();
        var movimiento = new CajaMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            Tipo = request.Tipo,
            MontoTotal = request.Monto,
            // Una SALIDA resta de la ganancia del negocio; ENTRADA/AJUSTE suman. Igual que el NestJS.
            GananciaNegocio = request.Tipo == TipoMovimientoCaja.SALIDA ? -request.Monto : request.Monto,
            GananciaRepartidor = 0,
            Descripcion = TrimOrNull(request.Descripcion) ?? $"Movimiento manual de {request.Tipo}",
            ConfirmadoPor = ActorSub(),
            FechaConfirmacion = now,
            CreatedAt = now,
        };
        _db.CajaMovimientos.Add(movimiento);
        await _db.SaveChangesAsync();

        return StatusCode(StatusCodes.Status201Created, MapMovimiento(MapDto(movimiento)));
    }

    [HttpPost("movimiento/{id}/anular")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> AnularMovimiento(string id, [FromBody] AnularMovimientoRequest request)
    {
        var movimiento = await _db.CajaMovimientos.FirstOrDefaultAsync(m => m.Id == id);
        if (movimiento is null)
        {
            return NotFound(new { message = "Movimiento no encontrado" });
        }
        if (movimiento.Anulado)
        {
            return BadRequest(new { message = "El movimiento ya estaba anulado" });
        }

        var motivo = TrimOrNull(request.Motivo) ?? "Sin motivo";
        movimiento.Anulado = true;
        movimiento.Descripcion = $"{movimiento.Descripcion} [ANULADO: {motivo}]";
        await _db.SaveChangesAsync();

        return Ok(MapMovimiento(MapDto(movimiento)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Lecturas
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resumen agregado del período (server-side) + lista de movimientos paginada (limit 100 por defecto).
    /// Mejora sobre el NestJS, que traía todos los movimientos y reducía en memoria: acá los cuatro totales
    /// salen de un solo agregado en Postgres y la lista se pagina. Filtra por <c>fechaConfirmacion</c> (AR).
    /// </summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] int? page = null,
        [FromQuery] int? limit = null)
    {
        var pageNum = page is > 0 ? page.Value : 1;
        var lim = Math.Clamp(limit ?? 100, 1, 200);

        var query = _db.CajaMovimientos.AsNoTracking().Where(m => !m.Anulado);
        if (DesdeArUtc(fechaInicio) is { } d) query = query.Where(m => m.FechaConfirmacion >= d);
        if (HastaArUtc(fechaFin) is { } h) query = query.Where(m => m.FechaConfirmacion <= h);

        var agg = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalEntradas = g.Sum(m => m.Tipo == TipoMovimientoCaja.ENTRADA ? m.MontoTotal : 0d),
                TotalSalidas = g.Sum(m => m.Tipo == TipoMovimientoCaja.SALIDA ? m.MontoTotal : 0d),
                GananciaNegocio = g.Sum(m => m.GananciaNegocio),
                GananciaRepartidor = g.Sum(m => m.GananciaRepartidor),
            })
            .FirstOrDefaultAsync();

        var resumen = agg is null
            ? new CajaResumen(0, 0, 0, 0, 0)
            : new CajaResumen(agg.TotalEntradas, agg.TotalSalidas, agg.GananciaNegocio, agg.GananciaRepartidor, agg.TotalEntradas - agg.TotalSalidas);

        var total = await query.CountAsync();
        var movimientos = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((pageNum - 1) * lim)
            .Take(lim)
            .Select(m => new CajaMovimientoResponse(
                m.Id, m.PedidoId, m.Tipo, m.MontoTotal, m.GananciaNegocio, m.GananciaRepartidor,
                m.Descripcion, m.ConfirmadoPor, m.FechaConfirmacion, m.Anulado, m.CreatedAt,
                m.Pedido == null ? null : new CajaMovimientoPedidoResponse(
                    m.Pedido.Id, m.Pedido.NombreCliente, m.Pedido.ApellidoCliente, m.Pedido.Total, m.Pedido.Estado)))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)lim);
        return Ok(new CajaResumenResponse(resumen, movimientos, total, pageNum, totalPages));
    }

    [HttpGet("pedido/{pedidoId}")]
    public async Task<IActionResult> MovimientosPorPedido(string pedidoId)
    {
        var movimientos = await _db.CajaMovimientos.AsNoTracking()
            .Where(m => m.PedidoId == pedidoId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new CajaMovimientoResponse(
                m.Id, m.PedidoId, m.Tipo, m.MontoTotal, m.GananciaNegocio, m.GananciaRepartidor,
                m.Descripcion, m.ConfirmadoPor, m.FechaConfirmacion, m.Anulado, m.CreatedAt,
                m.Pedido == null ? null : new CajaMovimientoPedidoResponse(
                    m.Pedido.Id, m.Pedido.NombreCliente, m.Pedido.ApellidoCliente, m.Pedido.Total, m.Pedido.Estado)))
            .ToListAsync();

        return Ok(movimientos);
    }

    /// <summary>
    /// Pedidos pendientes de cobro: sin pagar, no cancelados y sin ningún movimiento de caja. Projection a DTO
    /// (id, cliente, total, detalles) en vez de la entidad completa con include anidado. Filtra por <c>createdAt</c> (AR).
    /// </summary>
    [HttpGet("pendientes-cobro")]
    public async Task<IActionResult> PendientesCobro(
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null)
    {
        var query = _db.Pedidos.AsNoTracking().Where(p =>
            p.EstadoPago == EstadoPago.PENDIENTE
            && p.Estado != EstadoPedido.CANCELADO
            && !p.CajaMovimientos.Any());
        if (DesdeArUtc(fechaInicio) is { } d) query = query.Where(p => p.CreatedAt >= d);
        if (HastaArUtc(fechaFin) is { } h) query = query.Where(p => p.CreatedAt <= h);

        var pedidos = await query
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PendienteCobroResponse(
                p.Id, p.NombreCliente, p.ApellidoCliente, p.NumeroCliente, p.Total, p.CostoEnvio,
                p.Tipo, p.Estado, p.CreatedAt,
                p.PedidoDetalles.Select(dt => new PendienteCobroDetalle(
                    dt.Id, dt.Producto.Nombre, dt.Cantidad, dt.Subtotal)).ToList()))
            .ToListAsync();

        return Ok(pedidos);
    }

    [HttpGet("cuentas-abiertas-resumen")]
    public async Task<IActionResult> CuentasAbiertasResumen()
    {
        var cuentas = await _db.Pedidos.AsNoTracking()
            .Where(p => p.CuentaAbierta && p.EstadoPago == EstadoPago.PENDIENTE && p.Estado != EstadoPedido.CANCELADO)
            .Select(p => new CuentaAbiertaItem(p.Id, p.Total, p.NombreCliente, p.CreatedAt))
            .ToListAsync();

        return Ok(new CuentaAbiertaResumenResponse(cuentas.Count, cuentas.Sum(c => c.Total), cuentas));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static CajaMovimientoResponse MapMovimiento(CajaMovimientoDto m) => new(
        m.Id, m.PedidoId, m.Tipo, m.MontoTotal, m.GananciaNegocio, m.GananciaRepartidor,
        m.Descripcion, m.ConfirmadoPor, m.FechaConfirmacion, m.Anulado, m.CreatedAt, null);

    private static CajaMovimientoDto MapDto(CajaMovimiento m) => new(
        m.Id, m.PedidoId, m.Tipo, m.MontoTotal, m.GananciaNegocio, m.GananciaRepartidor,
        m.Descripcion, m.ConfirmadoPor, m.FechaConfirmacion, m.Anulado, m.CreatedAt);

    private string ActorSub() => User.FindFirst("sub")?.Value ?? string.Empty;

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static string? TrimOrNull(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Inicio del día AR (00:00 -03:00) como instante UTC.</summary>
    private static DateTime? DesdeArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date) : null;

    /// <summary>Fin del día AR (23:59:59.999… -03:00) como instante UTC.</summary>
    private static DateTime? HastaArUtc(string? value) =>
        TryParseDate(value, out var d) ? ArgentinaClock.ToUtc(d.Date.AddDays(1).AddTicks(-1)) : null;

    private static bool TryParseDate(string? value, out DateTime fecha)
    {
        fecha = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }
}
