using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrbIT.Api.Contracts.Dashboard;
using OrbIT.Application.Dashboard;
using OrbIT.Domain.MultiTenancy;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Dashboard del negocio (solo ADMIN). Reemplaza el <c>getMetrics</c> monolítico de NestJS por queries con
/// GroupBy/Sum server-side (ver <see cref="IDashboardService"/>). No es un agregador de otros endpoints: la
/// lógica vive en el servicio, que consulta la DB directamente. El aislamiento multi-tenant lo da el Global
/// Query Filter (scopeado por el claim del ADMIN).
/// </summary>
[ApiController]
[Route("dashboard")]
[Authorize(Roles = "ADMIN")]
public sealed class DashboardController : ControllerBase
{
    private readonly ITenantProvider _tenant;
    private readonly IDashboardService _dashboard;

    public DashboardController(ITenantProvider tenant, IDashboardService dashboard)
    {
        _tenant = tenant;
        _dashboard = dashboard;
    }

    /// <summary>
    /// Métricas del período. <c>desde</c> y <c>hasta</c> (fechas AR) son requeridas; el rango no puede exceder
    /// 90 días. Devuelve 400 si faltan/no parsean, si <c>desde &gt; hasta</c> o si el rango es demasiado grande.
    /// </summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics([FromQuery] string? desde = null, [FromQuery] string? hasta = null)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        try
        {
            var m = await _dashboard.GetMetricsAsync(desde, hasta, negocioId);
            return Ok(MapMetrics(m));
        }
        catch (DashboardException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    /// <summary>Datos en vivo del día: pedidos activos por estado, facturado de hoy, turno activo y cuentas abiertas.</summary>
    [HttpGet("resumen-hoy")]
    public async Task<IActionResult> ResumenHoy()
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var r = await _dashboard.GetResumenHoyAsync(negocioId);
        return Ok(new ResumenHoyResponse(
            r.PedidosActivos.Select(e => new MetricaEstadoResponse(e.Estado, e.Count)).ToList(),
            r.PedidosActivosTotal,
            r.FacturadoHoy,
            r.Turno is null ? null : new ResumenTurnoResponse(r.Turno.Id, r.Turno.HoraInicio, r.Turno.VentasEnVivo, r.Turno.MontoEsperado),
            r.CuentasAbiertasCount,
            r.CuentasAbiertasTotal));
    }

    private static MetricsResponse MapMetrics(MetricsResult m) => new(
        m.TotalFacturado,
        m.TotalNegocio,
        m.TotalDelivery,
        m.TotalPedidos,
        m.Promedio,
        m.Efectivo,
        m.Transferencia,
        m.Tarjeta,
        m.TotalGastos,
        m.GananciaNeta,
        m.PorDia.Select(d => new MetricaDiaResponse(d.Fecha, d.Pedidos, d.Total)).ToList(),
        m.PorHora.Select(h => new MetricaHoraResponse(h.Hora, h.Pedidos, h.Total)).ToList(),
        m.TopProductos.Select(p => new MetricaProductoResponse(p.Nombre, p.Cantidad, p.Total)).ToList(),
        m.PorTipo.Select(t => new MetricaTipoResponse(t.Tipo, t.Cantidad, t.Total)).ToList(),
        m.ClientesNuevos,
        m.ClientesRecurrentes,
        m.VentasSemanaAnterior,
        m.Comparativa,
        m.PedidosPorEstado.Select(e => new MetricaEstadoResponse(e.Estado, e.Count)).ToList());
}
