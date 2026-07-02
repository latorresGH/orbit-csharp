using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Insumos;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Common;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de insumos + motor de stock con auditoría (<c>StockMovimiento</c>), scopeado por negocio (tenant)
/// vía los Global Query Filters del <c>OrbitDbContext</c>. Es la base del inventario: lo consumen las
/// recetas de Productos (FK <c>Restrict</c>) y los Extras respaldados por insumo (FK <c>SetNull</c>).
///
/// <para><b>Alcance de esta tanda (A):</b> CRUD, stock (sumar/restar/ajuste vía PATCH), baja/alta,
/// <c>disponibilidad-productos</c> público, y las lecturas simples de movimientos (por insumo, recientes,
/// por extra/aderezo). Quedan para la Tanda B (junto con Turnos/Caja): <c>GET /movimientos</c> unificado,
/// <c>reporte/consumo</c> y <c>POST /disponibilidad</c>.</para>
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Stock atómico:</b> sumar/restar usan <c>ExecuteUpdate</c> con guarda de stock (el decremento
///   sólo afecta filas con stock suficiente), evitando perder updates concurrentes.</item>
///   <item><b>Validación de proveedor:</b> el <c>proveedorId</c> de create/update se valida estructuralmente
///   contra el tenant (NestJS lo conectaba sin scopear por negocio).</item>
///   <item><b>Borrado:</b> guard aplicativo si el insumo está en alguna receta (400 "usá baja lógica"),
///   con backstop por la FK <c>Restrict</c> (<c>23503</c>). Borrar un insumo libre desvincula extras y
///   movimientos (FK <c>SetNull</c>).</item>
///   <item><b>disponibilidad-productos:</b> público por <c>?negocio=slug</c> (<c>[AllowAnonymousWithTenant]</c>),
///   sólo expone <c>{ productoId, disponible }</c> — no revela inventario.</item>
/// </list>
/// </summary>
[ApiController]
[Route("insumos")]
[Authorize]
public sealed class InsumoController : ControllerBase
{
    private const string TipoAjusteManual = "AJUSTE_MANUAL";
    private const string TipoDescuentoPedido = "DESCUENTO_PEDIDO";
    private const string TipoAperturaTurno = "APERTURA_TURNO";
    private const string TipoCierreTurno = "CIERRE_TURNO";
    private const string ForeignKeyViolation = "23503";
    private const int MaxLimitMovimientos = 200;

    private static readonly string[] TiposTurno = { TipoAperturaTurno, TipoCierreTurno };

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public InsumoController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura pública (menú): tenant por claim (si hay sesión) o por ?negocio=slug.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("disponibilidad-productos")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> DisponibilidadProductos()
    {
        // Dos lecturas planas (sin N+1) + cruce en memoria, idéntico al NestJS pero sin exponer inventario.
        var productos = await _db.Productos.AsNoTracking()
            .Select(p => new
            {
                p.Id,
                Receta = p.ProductoReceta.Select(r => new { r.InsumoId, r.Cantidad }).ToList(),
            })
            .ToListAsync();

        var stockMap = await _db.Insumos.AsNoTracking()
            .Select(i => new { i.Id, i.StockActual })
            .ToDictionaryAsync(i => i.Id, i => i.StockActual);

        var resultado = productos.Select(p => new DisponibilidadProductoResponse(
            p.Id,
            p.Receta.Count == 0
                || p.Receta.All(r => stockMap.GetValueOrDefault(r.InsumoId) >= r.Cantidad)));

        return Ok(resultado);
    }

    /// <summary>
    /// Disponibilidad real de extras y aderezos: cuántas unidades se pueden vender según el stock (del insumo
    /// que respalda al extra, o el stock propio) y el consumo por unidad (específico por categoría si se pasa
    /// <c>categoriaId</c>; si hay categoría pero el extra/aderezo no tiene consumo configurado para ella →
    /// disponible=0). Público de menú.
    ///
    /// Divergencia con NestJS: allá era <c>@Public()</c> con <c>negocioId</c> en el body (que el frontend nunca
    /// mandaba → el endpoint quedaba roto). Acá el tenant se resuelve por claim o por <c>?negocio=slug</c> vía
    /// <c>[AllowAnonymousWithTenant]</c>, y el Global Query Filter scopea extras/aderezos/insumos al negocio.
    /// </summary>
    [HttpPost("disponibilidad")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> Disponibilidad([FromBody] CheckDisponibilidadRequest request)
    {
        var extraIds = request.ExtraIds ?? new List<string>();
        var aderezoIds = request.AderezoIds ?? new List<string>();
        var categoriaId = request.CategoriaId;

        var extras = extraIds.Count > 0
            ? await _db.Extras.AsNoTracking()
                .Where(e => extraIds.Contains(e.Id))
                .Select(e => new
                {
                    e.Id, e.Nombre, e.InsumoId, e.StockActual,
                    Consumos = e.ExtraConsumos.Select(c => new { c.CategoriaId, c.CantidadConsumo }).ToList(),
                })
                .ToListAsync()
            : [];

        var aderezos = aderezoIds.Count > 0
            ? await _db.Aderezos.AsNoTracking()
                .Where(a => aderezoIds.Contains(a.Id))
                .Select(a => new
                {
                    a.Id, a.Nombre, a.StockActual,
                    Consumos = a.AderezoConsumos.Select(c => new { c.CategoriaId, c.CantidadConsumo }).ToList(),
                })
                .ToListAsync()
            : [];

        // Stock real de los insumos que respaldan a los extras (los aderezos usan su propio stockActual).
        var insumoIds = extras.Where(e => e.InsumoId is not null).Select(e => e.InsumoId!).Distinct().ToList();
        var stockInsumos = insumoIds.Count > 0
            ? await _db.Insumos.AsNoTracking().Where(i => insumoIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.StockActual)
            : new Dictionary<string, double>();

        var resultado = new List<DisponibilidadItemResponse>(extras.Count + aderezos.Count);

        foreach (var extra in extras)
        {
            var stockActual = extra.InsumoId is not null
                ? stockInsumos.GetValueOrDefault(extra.InsumoId, 0)
                : extra.StockActual;

            var (consumo, sinConfig) = ResolverConsumo(
                categoriaId, extra.Consumos.Select(c => (c.CategoriaId, c.CantidadConsumo)));

            resultado.Add(new DisponibilidadItemResponse(
                extra.Id, extra.Nombre, "EXTRA", stockActual, consumo, CalcularDisponible(sinConfig, stockActual, consumo)));
        }

        foreach (var aderezo in aderezos)
        {
            var (consumo, sinConfig) = ResolverConsumo(
                categoriaId, aderezo.Consumos.Select(c => (c.CategoriaId, c.CantidadConsumo)));

            resultado.Add(new DisponibilidadItemResponse(
                aderezo.Id, aderezo.Nombre, "ADEREZO", aderezo.StockActual, consumo, CalcularDisponible(sinConfig, aderezo.StockActual, consumo)));
        }

        return Ok(resultado);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetAll([FromQuery] bool incluirInactivos = false)
    {
        var query = _db.Insumos.AsNoTracking().AsQueryable();
        if (!incluirInactivos)
        {
            query = query.Where(i => i.Activo);
        }

        var insumos = await ProjectToResponse(query.OrderBy(i => i.Nombre)).ToListAsync();
        return Ok(insumos);
    }

    [HttpGet("{id}", Name = nameof(GetInsumoById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetInsumoById(string id)
    {
        var insumo = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == id))
            .FirstOrDefaultAsync();
        return insumo is null ? NotFound() : Ok(insumo);
    }

    [HttpGet("{id}/movimientos")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetMovimientos(
        string id,
        [FromQuery] int limit = 50,
        [FromQuery] int page = 1)
    {
        limit = Math.Clamp(limit, 1, MaxLimitMovimientos);
        page = Math.Max(1, page);
        var skip = (page - 1) * limit;

        var baseQuery = _db.StockMovimientos.AsNoTracking().Where(m => m.InsumoId == id);
        var total = await baseQuery.CountAsync();
        var data = await baseQuery
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Take(limit)
            .Select(m => new StockMovimientoResponse(
                m.Id, m.InsumoId, m.ExtraId, m.AderezoId, m.Tipo, m.Cantidad,
                m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId, m.CreatedAt, null))
            .ToListAsync();

        return Ok(new PagedResult<StockMovimientoResponse>(data, total, page, TotalPages(total, limit)));
    }

    [HttpGet("movimientos/recientes")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetMovimientosRecientes([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, MaxLimitMovimientos);

        var data = await _db.StockMovimientos.AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new StockMovimientoResponse(
                m.Id, m.InsumoId, m.ExtraId, m.AderezoId, m.Tipo, m.Cantidad,
                m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId, m.CreatedAt,
                m.Insumo != null ? m.Insumo.Nombre : null))
            .ToListAsync();

        return Ok(data);
    }

    [HttpGet("movimientos/extra/{id}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetMovimientosPorExtra(string id, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, MaxLimitMovimientos);

        var data = await _db.StockMovimientos.AsNoTracking()
            .Where(m => m.ExtraId == id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new StockMovimientoResponse(
                m.Id, m.InsumoId, m.ExtraId, m.AderezoId, m.Tipo, m.Cantidad,
                m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId, m.CreatedAt, null))
            .ToListAsync();

        return Ok(data);
    }

    [HttpGet("movimientos/aderezo/{id}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetMovimientosPorAderezo(string id, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, MaxLimitMovimientos);

        var data = await _db.StockMovimientos.AsNoTracking()
            .Where(m => m.AderezoId == id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new StockMovimientoResponse(
                m.Id, m.InsumoId, m.ExtraId, m.AderezoId, m.Tipo, m.Cantidad,
                m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId, m.CreatedAt, null))
            .ToListAsync();

        return Ok(data);
    }

    /// <summary>
    /// Reporte unificado de movimientos: fusiona los <c>StockMovimiento</c> con los eventos de turno
    /// (apertura/cierre) en una sola lista paginada. Filtros: <c>desde</c>/<c>hasta</c> (fecha, límites en
    /// horario AR), <c>tipo</c> (un tipo de stock o <c>APERTURA_TURNO</c>/<c>CIERRE_TURNO</c>), <c>entidad</c>
    /// (<c>INSUMO</c>/<c>EXTRA</c>/<c>ADEREZO</c>). Réplica funcional del <c>obtenerMovimientosUnificados</c>
    /// de NestJS.
    ///
    /// <para>TODO (optimización diferida): la fusión + paginación es en memoria (como el NestJS): cuando no se
    /// filtra por <c>tipo</c>, se traen ~<c>ceil((skip+limit)/2)</c> turnos y se ordena/pagina el conjunto en
    /// memoria. Es una aproximación aceptable para reporting de uso ocasional sobre datasets acotados, pero en
    /// páginas profundas puede quedar corta. La versión correcta sería un <c>UNION ALL</c> server-side
    /// (StockMovimiento + apertura + cierre proyectados a columnas comunes) con <c>OFFSET/LIMIT</c> en SQL.
    /// No se implementa ahora a propósito (decisión del dueño): no vale la complejidad todavía.</para>
    /// </summary>
    [HttpGet("movimientos")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetMovimientosUnificados(
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 50,
        [FromQuery] string? tipo = null,
        [FromQuery] string? entidad = null)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, MaxLimitMovimientos);
        var skip = (page - 1) * limit;
        var fetchBound = skip + limit;

        var esTipoTurno = tipo is not null && TiposTurno.Contains(tipo);
        var esTipoStock = tipo is not null && !TiposTurno.Contains(tipo);

        var fechaDesde = ParseFechaAr(desde, endOfDay: false);
        var fechaHasta = ParseFechaAr(hasta, endOfDay: true);

        // ── Movimientos de stock ──────────────────────────────────────────────
        var stockItems = new List<MovimientoUnificadoResponse>();
        var stockTotal = 0;
        if (!esTipoTurno)
        {
            var stockQuery = _db.StockMovimientos.AsNoTracking().AsQueryable();
            if (fechaDesde is { } fd) stockQuery = stockQuery.Where(m => m.CreatedAt >= fd);
            if (fechaHasta is { } fh) stockQuery = stockQuery.Where(m => m.CreatedAt <= fh);
            if (tipo is not null) stockQuery = stockQuery.Where(m => m.Tipo == tipo);
            stockQuery = entidad switch
            {
                "INSUMO" => stockQuery.Where(m => m.InsumoId != null),
                "EXTRA" => stockQuery.Where(m => m.ExtraId != null),
                "ADEREZO" => stockQuery.Where(m => m.AderezoId != null),
                _ => stockQuery,
            };

            stockTotal = await stockQuery.CountAsync();

            // Modo "solo stock" (tipo de stock explícito): paginación server-side en DB. Si no, se traen
            // fetchBound filas para fusionarlas con los turnos y paginar en memoria.
            var stockRows = await stockQuery
                .OrderByDescending(m => m.CreatedAt)
                .Skip(esTipoStock ? skip : 0)
                .Take(esTipoStock ? limit : fetchBound)
                .Select(m => new
                {
                    m.Id, m.Tipo, m.CreatedAt, m.InsumoId, m.ExtraId, m.AderezoId,
                    m.Cantidad, m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId,
                    InsumoNombre = m.Insumo != null ? m.Insumo.Nombre : null,
                    InsumoUnidad = m.Insumo != null ? (UnidadMedida?)m.Insumo.UnidadMedida : null,
                })
                .ToListAsync();

            // Nombres de extra/aderezo por lookup (evita N+1): dos lecturas planas y cruce en memoria.
            var extraIds = stockRows.Where(m => m.ExtraId != null).Select(m => m.ExtraId!).Distinct().ToList();
            var aderezoIds = stockRows.Where(m => m.AderezoId != null).Select(m => m.AderezoId!).Distinct().ToList();
            var extraMap = extraIds.Count > 0
                ? await _db.Extras.AsNoTracking().Where(e => extraIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Nombre)
                : new Dictionary<string, string>();
            var aderezoMap = aderezoIds.Count > 0
                ? await _db.Aderezos.AsNoTracking().Where(a => aderezoIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Nombre)
                : new Dictionary<string, string>();

            stockItems.AddRange(stockRows.Select(m => new MovimientoUnificadoResponse(
                m.Id, m.Tipo, m.CreatedAt, m.InsumoId, m.ExtraId, m.AderezoId,
                m.Cantidad, m.StockAntes, m.StockDespues, m.PedidoId, m.Motivo, m.UserId,
                ConfirmadoPor: null, // StockMovimiento no tiene confirmadoPor (paridad NestJS: undefined→null).
                Insumo: m.InsumoNombre is not null ? new MovimientoUnificadoInsumo(m.InsumoNombre, m.InsumoUnidad!.Value) : null,
                ExtraNombre: m.ExtraId is not null ? extraMap.GetValueOrDefault(m.ExtraId) : null,
                AderezoNombre: m.AderezoId is not null ? aderezoMap.GetValueOrDefault(m.AderezoId) : null,
                MontoReal: null, MontoEsperado: null, Diferencia: null)));
        }

        // Sólo stock: la DB ya paginó, devolvemos directo.
        if (esTipoStock)
        {
            return Ok(new PagedResult<MovimientoUnificadoResponse>(stockItems, stockTotal, page, TotalPages(stockTotal, limit)));
        }

        // ── Eventos de turno (apertura + cierre) ──────────────────────────────
        var turnoItems = new List<MovimientoUnificadoResponse>();
        var turnoQuery = _db.Turnos.AsNoTracking().AsQueryable();
        if (tipo == TipoAperturaTurno)
        {
            if (fechaDesde is { } fd) turnoQuery = turnoQuery.Where(t => t.HoraInicio >= fd);
            if (fechaHasta is { } fh) turnoQuery = turnoQuery.Where(t => t.HoraInicio <= fh);
        }
        else if (tipo == TipoCierreTurno)
        {
            if (fechaDesde is { } fd) turnoQuery = turnoQuery.Where(t => t.HoraFin >= fd);
            if (fechaHasta is { } fh) turnoQuery = turnoQuery.Where(t => t.HoraFin <= fh);
            if (fechaDesde is null && fechaHasta is null) turnoQuery = turnoQuery.Where(t => t.HoraFin != null);
        }
        else if (fechaDesde is { } fd2 && fechaHasta is { } fh2)
        {
            turnoQuery = turnoQuery.Where(t =>
                (t.HoraInicio >= fd2 && t.HoraInicio <= fh2) ||
                (t.HoraFin >= fd2 && t.HoraFin <= fh2));
        }
        else if (fechaDesde is { } fd3)
        {
            turnoQuery = turnoQuery.Where(t => t.HoraInicio >= fd3 || t.HoraFin >= fd3);
        }
        else if (fechaHasta is { } fh3)
        {
            turnoQuery = turnoQuery.Where(t => t.HoraInicio <= fh3 || t.HoraFin <= fh3);
        }

        var turnos = await turnoQuery
            .OrderByDescending(t => t.HoraInicio)
            .Take((int)Math.Ceiling(fetchBound / 2.0))
            .Select(t => new
            {
                t.Id, t.HoraInicio, t.HoraFin, t.CajaAperturaMonto, t.CajaCierreMonto, t.VentasTotales,
                ConfirmadoPor = t.User.Nombre,
            })
            .ToListAsync();

        foreach (var turno in turnos)
        {
            if (tipo is null || tipo == TipoAperturaTurno)
            {
                var inRange = (fechaDesde is not { } fd || turno.HoraInicio >= fd)
                    && (fechaHasta is not { } fh || turno.HoraInicio <= fh);
                if (tipo is null || inRange)
                {
                    turnoItems.Add(EventoTurno(
                        $"{turno.Id}_apertura", TipoAperturaTurno, turno.HoraInicio, turno.ConfirmadoPor,
                        montoReal: turno.CajaAperturaMonto, montoEsperado: null, diferencia: null));
                }
            }

            if ((tipo is null || tipo == TipoCierreTurno) && turno.HoraFin is { } horaFin && turno.CajaCierreMonto is { } cierre)
            {
                var inRange = (fechaDesde is not { } fd || horaFin >= fd)
                    && (fechaHasta is not { } fh || horaFin <= fh);
                if (tipo is null || inRange)
                {
                    var montoEsperado = turno.CajaAperturaMonto + turno.VentasTotales;
                    turnoItems.Add(EventoTurno(
                        $"{turno.Id}_cierre", TipoCierreTurno, horaFin, turno.ConfirmadoPor,
                        montoReal: cierre, montoEsperado: montoEsperado, diferencia: cierre - montoEsperado));
                }
            }
        }

        // ── Fusión + paginación en memoria (ver TODO del método) ──────────────
        var todos = stockItems.Concat(turnoItems)
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
        var total = todos.Count;
        var data = todos.Skip(skip).Take(limit).ToList();

        return Ok(new PagedResult<MovimientoUnificadoResponse>(data, total, page, TotalPages(total, limit)));
    }

    /// <summary>
    /// Reporte de consumo por período: agrupa los movimientos <c>DESCUENTO_PEDIDO</c> por insumo y suma el
    /// consumo (GroupBy server-side, como en Gastos/Pedidos). Ordena por consumo descendente.
    ///
    /// Timezone AR para los límites <c>desde</c>/<c>hasta</c> (coherente con el resto del sistema). El NestJS
    /// original usaba <c>new Date(desde)</c> (medianoche UTC) + <c>setHours</c> local — inconsistente con su
    /// propio <c>/movimientos</c> (que sí usaba AR); acá se unifica a AR (mismo criterio que el reporte de
    /// Pedidos).
    /// </summary>
    [HttpGet("reporte/consumo")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> ReporteConsumo([FromQuery] string? desde, [FromQuery] string? hasta)
    {
        var start = ParseFechaAr(desde, endOfDay: false);
        var end = ParseFechaAr(hasta, endOfDay: true);
        if (start is null || end is null)
        {
            return BadRequest(new { message = "Los parámetros 'desde' y 'hasta' son obligatorios (formato fecha)." });
        }

        var agrupado = await _db.StockMovimientos.AsNoTracking()
            .Where(m => m.Tipo == TipoDescuentoPedido
                && m.InsumoId != null
                && m.CreatedAt >= start.Value
                && m.CreatedAt <= end.Value)
            .GroupBy(m => m.InsumoId!)
            .Select(g => new
            {
                InsumoId = g.Key,
                Suma = g.Sum(x => x.Cantidad),
                Cantidad = g.Count(),
            })
            .ToListAsync();

        if (agrupado.Count == 0)
        {
            return Ok(Array.Empty<ReporteConsumoItemResponse>());
        }

        var insumoIds = agrupado.Select(g => g.InsumoId).ToList();
        var insumoMap = await _db.Insumos.AsNoTracking()
            .Where(i => insumoIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Nombre, i.UnidadMedida })
            .ToDictionaryAsync(i => i.Id, i => i);

        var reporte = agrupado
            .Select(g =>
            {
                var info = insumoMap.GetValueOrDefault(g.InsumoId);
                return new ReporteConsumoItemResponse(
                    g.InsumoId,
                    info?.Nombre ?? "Insumo eliminado",
                    info?.UnidadMedida,
                    Math.Abs(g.Suma),
                    g.Cantidad);
            })
            .OrderByDescending(r => r.TotalConsumido)
            .ToList();

        return Ok(reporte);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN (salvo sumar, que también permite TRABAJADOR).
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateInsumoRequest request)
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

        var proveedorId = NullIfBlank(request.ProveedorId);
        if (await ValidarProveedor(proveedorId) is { } proveedorError)
        {
            return proveedorError;
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var insumo = new Insumo
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            StockActual = request.StockInicial,
            UnidadMedida = request.UnidadMedida,
            Activo = true,
            ProveedorId = proveedorId,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
            // StockMinimo se deja sin asignar: la columna tiene DEFAULT 5.0 en la DB (paridad NestJS).
        };
        _db.Insumos.Add(insumo);
        // Paridad con NestJS: el create NO registra un StockMovimiento por el stock inicial.
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == insumo.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetInsumoById), new { id = insumo.Id }, response);
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateInsumoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var insumo = await _db.Insumos.FirstOrDefaultAsync(i => i.Id == id);
        if (insumo is null)
        {
            return NotFound();
        }

        if (request.Nombre is not null)
        {
            insumo.Nombre = request.Nombre.Trim();
        }

        // proveedorId: ausente/null = no tocar; "" = desvincular; valor = vincular (validado).
        if (request.ProveedorId is not null)
        {
            var proveedorId = NullIfBlank(request.ProveedorId);
            if (await ValidarProveedor(proveedorId) is { } proveedorError)
            {
                return proveedorError;
            }
            insumo.ProveedorId = proveedorId;
        }

        double? stockAntes = null;
        if (request.StockActual is { } nuevoStock && nuevoStock != insumo.StockActual)
        {
            stockAntes = insumo.StockActual;
            insumo.StockActual = nuevoStock;
        }

        if (request.StockMinimo is { } stockMinimo) insumo.StockMinimo = stockMinimo;
        if (request.UnidadMedida is { } unidad) insumo.UnidadMedida = unidad;
        if (request.Activo is { } activo) insumo.Activo = activo;

        if (stockAntes is { } antes)
        {
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: insumo.StockActual - antes,
                stockAntes: antes,
                stockDespues: insumo.StockActual,
                motivo: "Edición manual desde admin",
                insumoId: id));
        }

        insumo.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/sumar")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> SumarStock(string id, [FromBody] InsumoStockMovRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!(request.Cantidad > 0))
        {
            return BadRequest(new { message = "Cantidad inválida" });
        }

        var stockAntes = await _db.Insumos.Where(i => i.Id == id)
            .Select(i => (double?)i.StockActual)
            .FirstOrDefaultAsync();
        if (stockAntes is null)
        {
            return NotFound(new { message = "Insumo no encontrado" });
        }

        await _db.Insumos.Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual + request.Cantidad));

        _db.StockMovimientos.Add(NuevoMovimiento(
            negocioId,
            cantidad: request.Cantidad,
            stockAntes: stockAntes.Value,
            stockDespues: stockAntes.Value + request.Cantidad,
            motivo: request.Motivo ?? "Ajuste manual de stock",
            insumoId: id));
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/restar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DescontarStock(string id, [FromBody] InsumoStockMovRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }
        if (!(request.Cantidad > 0))
        {
            return BadRequest(new { message = "Cantidad inválida" });
        }

        var stockAntes = await _db.Insumos.Where(i => i.Id == id)
            .Select(i => (double?)i.StockActual)
            .FirstOrDefaultAsync();
        if (stockAntes is null)
        {
            return NotFound(new { message = "Insumo no encontrado" });
        }

        // Decremento atómico con guarda: el UPDATE sólo afecta filas con stock suficiente.
        var filas = await _db.Insumos
            .Where(i => i.Id == id && i.StockActual >= request.Cantidad)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual - request.Cantidad));
        if (filas == 0)
        {
            return BadRequest(new
            {
                message = $"Stock insuficiente. Actual: {stockAntes.Value}, querés descontar: {request.Cantidad}",
            });
        }

        _db.StockMovimientos.Add(NuevoMovimiento(
            negocioId,
            cantidad: -request.Cantidad,
            stockAntes: stockAntes.Value,
            stockDespues: stockAntes.Value - request.Cantidad,
            motivo: request.Motivo ?? "Ajuste manual de stock",
            insumoId: id));
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/activo")]
    [Authorize(Roles = "ADMIN")]
    public Task<IActionResult> SetActivo(string id, [FromBody] SetInsumoActivoRequest request) =>
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
        var insumo = await _db.Insumos.FirstOrDefaultAsync(i => i.Id == id);
        if (insumo is null)
        {
            return NotFound();
        }

        // Guard aplicativo: un insumo en alguna receta no se borra (la FK ProductoReceta_insumoId es
        // Restrict y reventaría con 23503; lo anticipamos con un 400 explicativo). Los Extra.insumoId y
        // StockMovimiento.insumoId son SET NULL: borrar un insumo libre simplemente los desvincula.
        if (await _db.ProductoReceta.AnyAsync(r => r.InsumoId == id))
        {
            return BadRequest(new
            {
                message = "No se puede borrar un insumo que está en recetas. Usá baja lógica (activo=false).",
            });
        }

        _db.Insumos.Remove(insumo);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            // Backstop por si se agregó una receta entre el chequeo y el borrado (carrera).
            return BadRequest(new
            {
                message = "No se puede borrar un insumo que está en recetas. Usá baja lógica (activo=false).",
            });
        }

        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> CambiarActivo(string id, bool activo)
    {
        var insumo = await _db.Insumos.FirstOrDefaultAsync(i => i.Id == id);
        if (insumo is null)
        {
            return NotFound();
        }

        insumo.Activo = activo;
        insumo.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Insumos.AsNoTracking().Where(i => i.Id == id)).FirstAsync();
        return Ok(response);
    }

    /// <summary>Valida que el proveedor (si se indica) exista en el negocio activo. <c>null</c> ⇒ OK.</summary>
    private async Task<IActionResult?> ValidarProveedor(string? proveedorId)
    {
        if (string.IsNullOrWhiteSpace(proveedorId))
        {
            return null;
        }
        return await _db.Proveedors.AnyAsync(p => p.Id == proveedorId)
            ? null
            : BadRequest(new { message = $"Proveedor con ID {proveedorId} no encontrado." });
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int TotalPages(int total, int limit) => (int)Math.Ceiling(total / (double)limit);

    /// <summary>
    /// Convierte una fecha <c>yyyy-MM-dd</c> (u otra parseable) a instante UTC comparable contra
    /// <c>createdAt</c>, interpretando el día en horario AR: inicio → 00:00:00, fin → 23:59:59.999.
    /// Reemplaza el truco de NestJS de concatenar <c>+ 'T00:00:00.000-03:00'</c> / <c>'T23:59:59.999-03:00'</c>.
    /// </summary>
    private static DateTime? ParseFechaAr(string? fecha, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(fecha)
            || !DateTime.TryParse(fecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        var local = endOfDay
            ? parsed.Date.AddDays(1).AddMilliseconds(-1) // 23:59:59.999 del día
            : parsed.Date;                                // 00:00:00.000 del día
        return ArgentinaClock.ToUtc(local);
    }

    /// <summary>
    /// Resuelve el consumo por unidad de un extra/aderezo: 1 por defecto, o el <c>cantidadConsumo</c> específico
    /// de la categoría si se pasó <paramref name="categoriaId"/>. Si hay categoría pero no hay config para ella,
    /// devuelve <c>sinConfig=true</c> (⇒ disponible 0). Paridad con el NestJS.
    /// </summary>
    private static (double Consumo, bool SinConfig) ResolverConsumo(
        string? categoriaId, IEnumerable<(string CategoriaId, double CantidadConsumo)> consumos)
    {
        if (categoriaId is null)
        {
            return (1, false);
        }

        foreach (var c in consumos)
        {
            if (c.CategoriaId == categoriaId)
            {
                return (c.CantidadConsumo, false);
            }
        }

        return (1, true);
    }

    private static int CalcularDisponible(bool sinConfig, double stockActual, double consumo) =>
        sinConfig || consumo <= 0 ? 0 : (int)Math.Floor(stockActual / consumo);

    private static MovimientoUnificadoResponse EventoTurno(
        string id, string tipo, DateTime createdAt, string? confirmadoPor,
        double montoReal, double? montoEsperado, double? diferencia) => new(
            id, tipo, createdAt,
            InsumoId: null, ExtraId: null, AderezoId: null,
            Cantidad: 0, StockAntes: 0, StockDespues: 0,
            PedidoId: null, Motivo: null, UserId: null, ConfirmadoPor: confirmadoPor,
            Insumo: null, ExtraNombre: null, AderezoNombre: null,
            MontoReal: montoReal, MontoEsperado: montoEsperado, Diferencia: diferencia);

    private StockMovimiento NuevoMovimiento(
        string negocioId, double cantidad, double stockAntes, double stockDespues, string motivo,
        string insumoId) => new()
        {
            Id = Guid.NewGuid().ToString(),
            InsumoId = insumoId,
            NegocioId = negocioId,
            Tipo = TipoAjusteManual,
            Cantidad = cantidad,
            StockAntes = stockAntes,
            StockDespues = stockDespues,
            Motivo = motivo,
            UserId = User.FindFirst("sub")?.Value,
            // CreatedAt: lo genera la DB (DEFAULT CURRENT_TIMESTAMP via sentinel DateTime.MinValue).
        };

    private static IQueryable<InsumoResponse> ProjectToResponse(IQueryable<Insumo> query) =>
        query.Select(i => new InsumoResponse(
            i.Id,
            i.Nombre,
            i.UnidadMedida,
            i.StockActual,
            i.StockMinimo,
            i.Activo,
            i.ProveedorId,
            i.Proveedor != null ? i.Proveedor.Nombre : null,
            i.CreatedAt,
            i.UpdatedAt));

    private static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ForeignKeyViolation };
}
