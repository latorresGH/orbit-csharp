using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Pedidos;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Common;
using OrbIT.Application.Pedidos;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Módulo de pedidos (Tanda A: creación + gestión operativa). La creación y la cancelación —las dos
/// operaciones transaccionales y pesadas de stock— viven en <see cref="IPedidoService"/>
/// (OrbIT.Application); el resto de los endpoints son mutaciones cortas y lecturas que viven acá.
///
/// Tenant: todo va por los Global Query Filters salvo <see cref="Tracking"/> (público total, sin tenant,
/// con <c>IgnoreQueryFilters</c>) y <see cref="Crear"/> (público de menú vía
/// <see cref="AllowAnonymousWithTenantAttribute"/>). El reporting/stats/historial de Tanda B
/// (<see cref="Historial"/>, <see cref="Stats"/>, <see cref="StatsCocina"/>, <see cref="Reporte"/>) es de
/// solo lectura: projections server-side, GroupBy en Postgres y una query jsonb cruda para los extras.
/// </summary>
[ApiController]
[Route("pedidos")]
[Authorize]
public sealed class PedidosController : ControllerBase
{
    private static readonly EstadoPedido[] EstadosAbiertos =
    {
        EstadoPedido.PENDIENTE, EstadoPedido.EN_PREPARACION, EstadoPedido.EN_HORNO,
        EstadoPedido.LISTO_PARA_RETIRAR, EstadoPedido.EN_CAMINO,
    };

    // Grafo base (LOCAL / RETIRO). El override de DELIVERY está en GetTransiciones.
    private static readonly Dictionary<EstadoPedido, EstadoPedido[]> TransicionesBase = new()
    {
        [EstadoPedido.PENDIENTE] = new[] { EstadoPedido.EN_PREPARACION, EstadoPedido.CANCELADO },
        [EstadoPedido.EN_PREPARACION] = new[] { EstadoPedido.EN_HORNO, EstadoPedido.LISTO_PARA_RETIRAR, EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
        [EstadoPedido.EN_HORNO] = new[] { EstadoPedido.LISTO_PARA_RETIRAR, EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
        [EstadoPedido.LISTO_PARA_RETIRAR] = new[] { EstadoPedido.ENTREGADO, EstadoPedido.CANCELADO },
        [EstadoPedido.EN_CAMINO] = new[] { EstadoPedido.ENTREGADO, EstadoPedido.PROBLEMA_DIRECCION, EstadoPedido.CANCELADO },
        [EstadoPedido.ENTREGADO] = Array.Empty<EstadoPedido>(),
        [EstadoPedido.CANCELADO] = Array.Empty<EstadoPedido>(),
        [EstadoPedido.PROBLEMA_DIRECCION] = new[] { EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
    };

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IPedidoService _pedidos;
    private readonly IPedidoNotificationService _notifier;

    public PedidosController(OrbitDbContext db, ITenantProvider tenant, IPedidoService pedidos, IPedidoNotificationService notifier)
    {
        _db = db;
        _tenant = tenant;
        _pedidos = pedidos;
        _notifier = notifier;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Creación (público de menú)
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost]
    [AllowAnonymousWithTenant]
    [EnableRateLimiting("pedidos-create")]
    public async Task<IActionResult> Crear([FromBody] CreatePedidoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var esAutenticado = User.Identity?.IsAuthenticated == true;
        var input = MapToInput(request, esAutenticado);

        string pedidoId;
        try
        {
            pedidoId = await _pedidos.CrearPedidoAsync(input, negocioId);
        }
        catch (PedidoException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }

        var response = await CargarPedidoResponseAsync(pedidoId);

        // TODO (SignalR PedidosHub): si request.Origen == "MENU", emitir por IHubContext<PedidosHub> el
        // evento 'nuevo-pedido' a la room = negocioId, con payload:
        //   { id, nombreCliente, apellidoCliente, numeroCliente, tipo, total, timestamp = DateTime.UtcNow }
        // Se cablea cuando se implemente el PedidosHub.

        return StatusCode(StatusCodes.Status201Created, response);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Lecturas
    // ═════════════════════════════════════════════════════════════════════════

    [HttpGet]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Listar(
        [FromQuery] EstadoPedido? estado = null,
        [FromQuery] TipoPedido? tipo = null,
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null)
    {
        var query = PedidosConGrafo();
        if (estado is { } e) query = query.Where(p => p.Estado == e);
        if (tipo is { } t) query = query.Where(p => p.Tipo == t);
        if (TryParseDesde(desde, out var d)) query = query.Where(p => p.CreatedAt >= d);
        if (TryParseHasta(hasta, out var h)) query = query.Where(p => p.CreatedAt <= h);

        var pedidos = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Ok(pedidos.Select(MapPedido));
    }

    [HttpGet("delivery-pendientes")]
    [Authorize(Roles = "ADMIN,DELIVERY")]
    public async Task<IActionResult> DeliveryPendientes()
    {
        var query = PedidosConGrafo()
            .Where(p => p.Tipo == TipoPedido.DELIVERY && EstadosAbiertos.Contains(p.Estado));

        // Un repartidor solo ve los suyos.
        if (User.IsInRole("DELIVERY"))
        {
            var sub = User.FindFirst("sub")?.Value;
            query = query.Where(p => p.RepartidorId == sub);
        }

        var pedidos = await query.OrderBy(p => p.CreatedAt).ToListAsync();
        return Ok(pedidos.Select(MapPedido));
    }

    [HttpGet("cuentas-abiertas")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> CuentasAbiertas()
    {
        var pedidos = await PedidosConGrafo()
            .Where(p => p.CuentaAbierta && p.EstadoPago == EstadoPago.PENDIENTE && p.Estado != EstadoPedido.CANCELADO)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        return Ok(pedidos.Select(MapPedido));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Reporting / stats / historial (Tanda B — solo lectura)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Historial paginado (offset). Filtros opcionales: rango de fechas (AR -03:00), tipo, estado y búsqueda
    /// libre (ILIKE sobre nombre/apellido/número/dirección/id). Projection liviana a DTO en vez de la entidad
    /// completa con triple include del NestJS.
    /// </summary>
    [HttpGet("historial")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Historial(
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] int? page = null,
        [FromQuery] int? limit = null,
        [FromQuery] TipoPedido? tipo = null,
        [FromQuery] EstadoPedido? estado = null,
        [FromQuery] string? busqueda = null)
    {
        var pageNum = page is > 0 ? page.Value : 1;
        var lim = Math.Clamp(limit ?? 50, 1, 200);

        var query = _db.Pedidos.AsNoTracking().AsQueryable();
        if (ArgentinaClock.DesdeArUtc(desde) is { } d) query = query.Where(p => p.CreatedAt >= d);
        if (ArgentinaClock.HastaArUtc(hasta) is { } h) query = query.Where(p => p.CreatedAt <= h);
        if (tipo is { } t) query = query.Where(p => p.Tipo == t);
        if (estado is { } e) query = query.Where(p => p.Estado == e);

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            var q = busqueda.Trim();
            if (q.Length > 100) q = q[..100];
            var pattern = "%" + EscapeLike(q) + "%";
            query = query.Where(p =>
                EF.Functions.ILike(p.NombreCliente!, pattern, "\\") ||
                EF.Functions.ILike(p.ApellidoCliente!, pattern, "\\") ||
                EF.Functions.ILike(p.NumeroCliente!, pattern, "\\") ||
                EF.Functions.ILike(p.Direccion!, pattern, "\\") ||
                EF.Functions.ILike(p.Id, pattern, "\\"));
        }

        var total = await query.CountAsync();
        var data = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((pageNum - 1) * lim)
            .Take(lim)
            .Select(p => new HistorialPedidoResponse(
                p.Id, p.Tipo, p.Estado, p.EstadoPago, p.MetodoPago, p.Total, p.CostoEnvio,
                p.NombreCliente, p.ApellidoCliente, p.NumeroCliente, p.Direccion,
                p.RepartidorId, p.Repartidor != null ? p.Repartidor.Nombre : null,
                p.CuentaAbierta, p.CreatedAt,
                p.PedidoDetalles.Select(dt => new HistorialDetalleResponse(
                    dt.Id, dt.Producto.Nombre, dt.Cantidad, dt.PrecioUnitario, dt.Subtotal)).ToList()))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)lim);
        return Ok(new PaginatedResponse<HistorialPedidoResponse>(data, total, pageNum, totalPages));
    }

    /// <summary>
    /// Estadísticas operativas del período: pedidos por hora (bucket en hora AR), por tipo, top-5 motivos de
    /// cancelación y top-10 repartidores. Los conteos por tipo/repartidor se agregan con GroupBy en Postgres
    /// (mejora sobre el NestJS, que los contaba en memoria con findMany + Map).
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Stats([FromQuery] string? desde = null, [FromQuery] string? hasta = null)
    {
        var baseQuery = _db.Pedidos.AsNoTracking().AsQueryable();
        if (ArgentinaClock.DesdeArUtc(desde) is { } d) baseQuery = baseQuery.Where(p => p.CreatedAt >= d);
        if (ArgentinaClock.HastaArUtc(hasta) is { } h) baseQuery = baseQuery.Where(p => p.CreatedAt <= h);

        // porHora: hay que llevar cada createdAt (UTC) a hora AR, así que se traen sólo las fechas y se
        // bucketean en memoria (igual que el NestJS: la conversión de zona no es trivial en SQL).
        var fechas = await baseQuery.Select(p => p.CreatedAt).ToListAsync();
        var counts = new int[24];
        foreach (var f in fechas) counts[ArgentinaClock.ToLocal(f).Hour]++;
        var porHora = Enumerable.Range(0, 24)
            .Where(hora => counts[hora] > 0)
            .Select(hora => new StatHora(hora, counts[hora]))
            .ToList();

        // porTipo: GroupBy server-side.
        var porTipo = await baseQuery
            .GroupBy(p => p.Tipo)
            .Select(g => new StatTipo(g.Key, g.Count()))
            .ToListAsync();

        // cancelaciones: el conteo por motivo crudo se hace en Postgres; la normalización JS
        // (trim → 'Sin motivo' → substring(0,22) → merge) es una post-agregación en memoria sobre pocas filas.
        var canceladosRaw = await baseQuery
            .Where(p => p.Estado == EstadoPedido.CANCELADO)
            .GroupBy(p => p.MotivoCancelacion)
            .Select(g => new { Motivo = g.Key, Count = g.Count() })
            .ToListAsync();
        var motivos = new Dictionary<string, int>();
        foreach (var r in canceladosRaw)
        {
            var m = string.IsNullOrWhiteSpace(r.Motivo) ? "Sin motivo" : r.Motivo.Trim();
            if (m.Length > 22) m = m[..22];
            motivos[m] = motivos.GetValueOrDefault(m) + r.Count;
        }
        var cancelaciones = motivos
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new StatMotivo(kv.Key, kv.Value))
            .ToList();

        // repartidores: GroupBy server-side por id; los nombres se resuelven en un segundo query y se
        // reensamblan preservando el orden por conteo (evita GroupBy sobre columna de navegación).
        var repRaw = await baseQuery
            .Where(p => p.Tipo == TipoPedido.DELIVERY && p.Estado == EstadoPedido.ENTREGADO && p.RepartidorId != null)
            .GroupBy(p => p.RepartidorId!)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync();
        var ids = repRaw.Select(x => x.Id).ToList();
        var nombres = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Nombre })
            .ToDictionaryAsync(u => u.Id, u => u.Nombre);
        var repartidores = repRaw
            .Select(x => new StatRepartidor(nombres.GetValueOrDefault(x.Id) ?? "", x.Count))
            .ToList();

        return Ok(new StatsResponse(porHora, porTipo, cancelaciones, repartidores));
    }

    /// <summary>
    /// Estadísticas de cocina: extras (snapshot jsonb) y aderezos (M:N) más pedidos del período.
    /// Los extras se agregan con una query jsonb cruda en Postgres (<c>jsonb_array_elements</c> + GROUP BY),
    /// la mejora central de la tanda: evita traer todos los snapshots a memoria. Los aderezos se agregan con
    /// SelectMany + GroupBy server-side sobre la relación M:N.
    /// </summary>
    [HttpGet("stats/cocina")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> StatsCocina([FromQuery] string? desde = null, [FromQuery] string? hasta = null)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var d = ArgentinaClock.DesdeArUtc(desde);
        var h = ArgentinaClock.HastaArUtc(hasta);

        var extrasTop = await ExtrasTopAsync(negocioId, d, h);

        var detalles = _db.PedidoDetalles.AsNoTracking().AsQueryable();
        if (d is { } dd) detalles = detalles.Where(x => x.Pedido.CreatedAt >= dd);
        if (h is { } hh) detalles = detalles.Where(x => x.Pedido.CreatedAt <= hh);
        var aderezosRaw = await detalles
            .SelectMany(x => x.As)
            .GroupBy(a => a.Nombre)
            .Select(g => new { Nombre = g.Key, Cantidad = g.Count() })
            .OrderByDescending(x => x.Cantidad)
            .ThenBy(x => x.Nombre)
            .Take(10)
            .ToListAsync();
        var aderezosTop = aderezosRaw.Select(x => new CocinaItem(x.Nombre, x.Cantidad)).ToList();

        return Ok(new StatsCocinaResponse(extrasTop, aderezosTop));
    }

    /// <summary>
    /// Reporte financiero del período: sólo pedidos <see cref="EstadoPedido.ENTREGADO"/>. Devuelve total
    /// facturado, cantidad, promedio, corte efectivo/transferencia, desglose por día (AR) y top-10 productos.
    /// </summary>
    [HttpGet("reporte")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Reporte([FromQuery] string? desde = null, [FromQuery] string? hasta = null)
    {
        // NestJS usaba UTC crudo acá (new Date(desde) sin offset + hasta con sufijo 'Z'), inconsistente con
        // historial/stats/cocina que usan -03:00. Casi seguro un bug: unificado a AR (-03:00) para todo Tanda B.
        var query = _db.Pedidos.AsNoTracking().Where(p => p.Estado == EstadoPedido.ENTREGADO);
        if (ArgentinaClock.DesdeArUtc(desde) is { } d) query = query.Where(p => p.CreatedAt >= d);
        if (ArgentinaClock.HastaArUtc(hasta) is { } h) query = query.Where(p => p.CreatedAt <= h);

        var pedidos = await query
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.Total,
                p.MetodoPago,
                p.CreatedAt,
                Detalles = p.PedidoDetalles.Select(dt => new
                {
                    Nombre = dt.Producto.Nombre,
                    dt.Cantidad,
                    dt.Subtotal,
                }).ToList(),
            })
            .ToListAsync();

        double totalFacturado = 0, efectivo = 0, transferencia = 0;
        var porDia = new Dictionary<string, (int Pedidos, double Total)>();
        var porProducto = new Dictionary<string, (int Cantidad, double Total)>();

        foreach (var p in pedidos)
        {
            totalFacturado += p.Total;
            if (p.MetodoPago == MetodoPago.EFECTIVO) efectivo += p.Total;
            if (p.MetodoPago == MetodoPago.TRANSFERENCIA) transferencia += p.Total;

            var dia = ArgentinaClock.ToLocal(p.CreatedAt).ToString("yyyy-MM-dd");
            var acc = porDia.GetValueOrDefault(dia);
            porDia[dia] = (acc.Pedidos + 1, acc.Total + p.Total);

            foreach (var dt in p.Detalles)
            {
                var nombre = string.IsNullOrEmpty(dt.Nombre) ? "Desconocido" : dt.Nombre;
                var pp = porProducto.GetValueOrDefault(nombre);
                porProducto[nombre] = (pp.Cantidad + dt.Cantidad, pp.Total + dt.Subtotal);
            }
        }

        var response = new ReporteResponse(
            totalFacturado,
            pedidos.Count,
            pedidos.Count > 0 ? totalFacturado / pedidos.Count : 0,
            efectivo,
            transferencia,
            porDia.OrderBy(kv => kv.Key).Select(kv => new ReporteDia(kv.Key, kv.Value.Pedidos, kv.Value.Total)).ToList(),
            porProducto.OrderByDescending(kv => kv.Value.Cantidad).Take(10)
                .Select(kv => new ReporteProducto(kv.Key, kv.Value.Cantidad, kv.Value.Total)).ToList());

        return Ok(response);
    }

    [HttpGet("{id}/tracking")]
    [AllowAnonymous]
    public async Task<IActionResult> Tracking(string id)
    {
        // Público total: sin tenant, id opaco. IgnoreQueryFilters porque no hay tenant resuelto.
        var pedido = await _db.Pedidos.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id, p.Estado, p.Tipo, p.DemoraEstimadaMin, p.Total, p.CostoEnvio, p.RepartidorId,
                p.NombreCliente, p.Direccion,
                Detalles = p.PedidoDetalles.Select(d => new { d.Cantidad, Nombre = d.Producto.Nombre }).ToList(),
            })
            .FirstOrDefaultAsync();
        if (pedido is null)
        {
            return NotFound();
        }

        return Ok(new TrackingResponse(
            pedido.Id, pedido.Estado, pedido.Tipo, pedido.DemoraEstimadaMin, pedido.Total, pedido.CostoEnvio,
            pedido.RepartidorId is not null, pedido.NombreCliente,
            pedido.Tipo == TipoPedido.DELIVERY ? pedido.Direccion : null,
            pedido.Detalles.Select(d => new TrackingDetalleResponse(d.Cantidad, d.Nombre)).ToList()));
    }

    [HttpGet("{id}", Name = nameof(GetPedidoById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR,DELIVERY")]
    public async Task<IActionResult> GetPedidoById(string id)
    {
        var response = await CargarPedidoResponseAsync(id);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("{id}/detalles-no-impresos")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> DetallesNoImpresos(string id)
    {
        if (!await _db.Pedidos.AnyAsync(p => p.Id == id))
        {
            return NotFound();
        }
        var detalles = await _db.PedidoDetalles.AsNoTracking()
            .Where(d => d.PedidoId == id && !d.ImpresoEnCocina)
            .Include(d => d.Producto)
            .Include(d => d.As)
            .Include(d => d.PizzaMediaMedium)
            .ToListAsync();
        return Ok(detalles.Select(MapDetalle));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Mutaciones operativas
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPatch("{id}/estado")]
    [Authorize(Roles = "ADMIN,TRABAJADOR,DELIVERY")]
    public async Task<IActionResult> CambiarEstado(string id, [FromBody] CambiarEstadoRequest request)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado is EstadoPedido.ENTREGADO or EstadoPedido.CANCELADO)
        {
            return BadRequest(new { message = "No se puede cambiar estado de un pedido cerrado" });
        }

        var permitidas = GetTransiciones(pedido.Tipo, pedido.Estado);
        if (!permitidas.Contains(request.Estado))
        {
            return BadRequest(new
            {
                message = $"No se puede pasar de {pedido.Estado} a {request.Estado}. Transiciones permitidas: {string.Join(", ", permitidas)}",
            });
        }

        var estadoAnterior = pedido.Estado;
        pedido.Estado = request.Estado;
        await _db.SaveChangesAsync();

        // SignalR (best-effort, ya commiteado): notifica al panel el cambio de estado para refrescar la tarjeta
        // sin recargar el listado. El notifier traga errores; una falla de emisión no rompe el cambio guardado.
        await _notifier.NotificarActualizacionAsync(pedido.NegocioId, new PedidoActualizadoNotification(
            pedido.Id,
            pedido.Estado.ToString(),
            estadoAnterior.ToString(),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));

        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPatch("{id}/finalizar")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Finalizar(string id)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado == EstadoPedido.ENTREGADO) return BadRequest(new { message = "El pedido ya está entregado" });
        if (pedido.Estado == EstadoPedido.CANCELADO) return BadRequest(new { message = "No se puede finalizar un pedido cancelado" });

        pedido.Estado = EstadoPedido.ENTREGADO;
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPost("{id}/cancelar")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Cancelar(string id, [FromBody] CancelarPedidoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        try
        {
            // El rol viene del claim del actor, NO del body (un empleado no declara quién canceló).
            await _pedidos.CancelarPedidoAsync(id, request.Motivo, ActorRol(), negocioId);
        }
        catch (PedidoException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }

        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPatch("{id}/pago")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> SetPago(string id, [FromBody] SetPagoRequest request)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado is EstadoPedido.ENTREGADO or EstadoPedido.CANCELADO)
        {
            return BadRequest(new { message = "No se puede cambiar pago de un pedido cerrado" });
        }

        if (request.MetodoPago is { } mp) pedido.MetodoPago = mp;
        if (request.NumeroCliente is not null) pedido.NumeroCliente = TrimOrNull(request.NumeroCliente);
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPatch("{id}/costo-envio")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> SetCostoEnvio(string id, [FromBody] SetCostoEnvioRequest request)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado is EstadoPedido.ENTREGADO or EstadoPedido.CANCELADO)
        {
            return BadRequest(new { message = "No se puede modificar el costo de envío de un pedido cerrado" });
        }

        pedido.CostoEnvio = request.CostoEnvio;
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPatch("{id}/asignar")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Asignar(string id, [FromBody] AsignarRepartidorRequest request)
    {
        var negocioId = _tenant.NegocioId;
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado is EstadoPedido.ENTREGADO or EstadoPedido.CANCELADO)
        {
            return BadRequest(new { message = "No se puede modificar un pedido cerrado" });
        }

        if (!string.IsNullOrEmpty(request.RepartidorId))
        {
            var pertenece = await _db.Users.AnyAsync(u => u.Id == request.RepartidorId && u.NegocioId == negocioId);
            if (!pertenece)
            {
                return NotFound(new { message = "Repartidor no encontrado" });
            }
            pedido.RepartidorId = request.RepartidorId;
        }
        else if (request.RepartidorId is not null)
        {
            pedido.RepartidorId = null; // "" → desasignar
        }

        if (request.CostoEnvio is { } costo) pedido.CostoEnvio = costo;
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPost("{id}/anular")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Anular(string id, [FromBody] AnularCuentaRequest request)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.EstadoPago == EstadoPago.ANULADO) return BadRequest(new { message = "La cuenta ya estaba anulada" });
        if (pedido.EstadoPago == EstadoPago.PAGADO) return BadRequest(new { message = "No se puede anular una cuenta ya pagada" });

        pedido.EstadoPago = EstadoPago.ANULADO;
        pedido.CuentaAbierta = false;
        pedido.MotivoCancelacion = request.Motivo.Trim();
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPatch("{id}/cerrar-cuenta")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> CerrarCuenta(string id)
    {
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == id);
        if (pedido is null)
        {
            return NotFound();
        }
        if (!pedido.CuentaAbierta) return BadRequest(new { message = "El pedido no es una cuenta abierta" });
        if (pedido.EstadoPago != EstadoPago.PENDIENTE) return BadRequest(new { message = "Solo se pueden cerrar cuentas pendientes" });

        pedido.CuentaAbierta = false;
        await _db.SaveChangesAsync();
        return Ok(await CargarPedidoResponseAsync(id));
    }

    [HttpPost("{id}/imprimir")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Imprimir(string id)
    {
        var pedido = await _db.Pedidos.AsNoTracking()
            .Where(p => p.Id == id).Select(p => new { p.Estado }).FirstOrDefaultAsync();
        if (pedido is null)
        {
            return NotFound();
        }
        if (pedido.Estado is EstadoPedido.CANCELADO or EstadoPedido.ENTREGADO)
        {
            return BadRequest(new { message = "No se puede imprimir un pedido cerrado" });
        }

        var marcados = await _db.PedidoDetalles
            .Where(d => d.PedidoId == id && !d.ImpresoEnCocina)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ImpresoEnCocina, true));
        return Ok(new ImprimirResponse(marcados));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private IQueryable<Pedido> PedidosConGrafo() =>
        _db.Pedidos.AsNoTracking()
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.Producto)
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.As)
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.PizzaMediaMedium);

    private async Task<PedidoResponse?> CargarPedidoResponseAsync(string id)
    {
        var pedido = await PedidosConGrafo().FirstOrDefaultAsync(p => p.Id == id);
        return pedido is null ? null : MapPedido(pedido);
    }

    private static EstadoPedido[] GetTransiciones(TipoPedido tipo, EstadoPedido estado)
    {
        if (tipo == TipoPedido.DELIVERY)
        {
            return estado switch
            {
                EstadoPedido.PENDIENTE => new[] { EstadoPedido.EN_PREPARACION, EstadoPedido.EN_HORNO, EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
                EstadoPedido.EN_PREPARACION => new[] { EstadoPedido.EN_HORNO, EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
                EstadoPedido.EN_HORNO => new[] { EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
                EstadoPedido.EN_CAMINO => new[] { EstadoPedido.ENTREGADO, EstadoPedido.PROBLEMA_DIRECCION, EstadoPedido.CANCELADO },
                EstadoPedido.PROBLEMA_DIRECCION => new[] { EstadoPedido.EN_CAMINO, EstadoPedido.CANCELADO },
                _ => Array.Empty<EstadoPedido>(),
            };
        }
        return TransicionesBase.GetValueOrDefault(estado, Array.Empty<EstadoPedido>());
    }

    private Role ActorRol()
    {
        var claim = User.FindFirst("role")?.Value;
        return Enum.TryParse<Role>(claim, out var rol) ? rol : Role.TRABAJADOR;
    }

    private static CrearPedidoInput MapToInput(CreatePedidoRequest r, bool esAutenticado) => new(
        r.Tipo, r.NombreCliente, r.ApellidoCliente, r.NumeroCliente, r.MetodoPago, r.Direccion, r.CostoEnvio,
        r.DireccionLat, r.DireccionLng, r.DireccionFormateada, r.Piso, r.Departamento, r.Referencias,
        r.NotasRepartidor, r.ShippingZoneName, r.ShippingReason, r.DireccionPrecision, r.PedidoId,
        r.CuentaAbierta ?? false, r.EstadoPago, r.RepartidorId, r.MesaId, r.Origen, r.CodigoDescuento,
        r.Detalles.Select(d => new PedidoLineaInput(
            d.ProductoId, d.Cantidad, d.Notas, d.PrecioUnitario,
            d.Extras?.Select(e => new PedidoExtraInput(e.ExtraId, e.Cantidad ?? 1)).ToList(),
            d.AderezosIds, d.SinExtras ?? false, d.ImpresoEnCocina ?? false,
            d.MediaMedia is null ? null : new MediaMediaInput(d.MediaMedia.Sabor1Id, d.MediaMedia.Sabor2Id))).ToList(),
        esAutenticado);

    private static PedidoResponse MapPedido(Pedido p) => new(
        p.Id, p.Tipo, p.Estado, p.EstadoPago, p.MetodoPago, p.Total, p.CostoEnvio, p.Direccion,
        p.NombreCliente, p.ApellidoCliente, p.NumeroCliente, p.ClienteId, p.MesaId, p.RepartidorId,
        p.CuentaAbierta, p.DemoraEstimadaMin, p.MotivoCancelacion, p.CanceladoPor, p.CreatedAt,
        p.PedidoDetalles.Select(MapDetalle).ToList());

    private static PedidoDetalleResponse MapDetalle(PedidoDetalle d) => new(
        d.Id, d.ProductoId, d.Producto?.Nombre, d.Cantidad, d.PrecioUnitario, d.Subtotal, d.Notas,
        d.SinExtras, d.ImpresoEnCocina,
        DeserializeExtras(d.Extras),
        d.As.Select(a => new AderezoResponse(a.Id, a.Nombre)).ToList(),
        d.PizzaMediaMedium is null ? null : new MediaMediaResponse(d.PizzaMediaMedium.Sabor1Id, d.PizzaMediaMedium.Sabor2Id));

    private static IReadOnlyList<ExtraSnapshotResponse> DeserializeExtras(string? json) =>
        string.IsNullOrEmpty(json)
            ? Array.Empty<ExtraSnapshotResponse>()
            : JsonSerializer.Deserialize<List<ExtraSnapshotResponse>>(json) ?? new List<ExtraSnapshotResponse>();

    /// <summary>
    /// Top-10 extras del período agregando el snapshot jsonb en Postgres. <c>jsonb_array_elements</c> expande
    /// el array de cada detalle y se agrupa por <c>Nombre</c> (clave PascalCase del snapshot serializado por
    /// <c>PedidoService</c>). El <c>CASE ... 'array'</c> blinda contra columnas null o no-array (no revientan).
    /// Multi-tenant explícito por <c>negocioId</c>: la query cruda no pasa por el Global Query Filter.
    ///
    /// Equivalente en memoria (fallback / referencia): traer <c>extras</c> de los detalles del período,
    /// deserializar cada array y sumar +1 por elemento agrupando por <c>Nombre</c>. Se prefiere el SQL porque
    /// evita transferir todos los snapshots; el resultado es idéntico.
    /// </summary>
    private async Task<List<CocinaItem>> ExtrasTopAsync(string negocioId, DateTime? desde, DateTime? hasta)
    {
        FormattableString sql = $"""
            SELECT elem->>'Nombre' AS "Nombre", COUNT(*)::int AS "Cantidad"
            FROM "PedidoDetalle" d
            JOIN "Pedido" p ON p."id" = d."pedidoId"
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE WHEN jsonb_typeof(d."extras") = 'array' THEN d."extras" ELSE '[]'::jsonb END
            ) AS elem
            WHERE d."negocioId" = {negocioId}
              AND ({desde}::timestamp IS NULL OR p."createdAt" >= {desde})
              AND ({hasta}::timestamp IS NULL OR p."createdAt" <= {hasta})
              AND NULLIF(elem->>'Nombre', '') IS NOT NULL
            GROUP BY elem->>'Nombre'
            ORDER BY COUNT(*) DESC, elem->>'Nombre'
            LIMIT 10
            """;

        return await _db.Database.SqlQuery<CocinaItem>(sql).ToListAsync();
    }

    /// <summary>Escapa los comodines de LIKE/ILIKE (<c>\ % _</c>) para tratar la búsqueda como substring literal.</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static bool TryParseDesde(string? value, out DateTime fecha)
    {
        if (!string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            fecha = DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified);
            return true;
        }
        fecha = default;
        return false;
    }

    private static bool TryParseHasta(string? value, out DateTime fecha)
    {
        if (!string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            fecha = DateTime.SpecifyKind(d.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified);
            return true;
        }
        fecha = default;
        return false;
    }

    private static string? TrimOrNull(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }
}
