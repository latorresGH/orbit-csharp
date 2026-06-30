using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Insumos;
using OrbIT.Api.MultiTenancy;
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
    private const string ForeignKeyViolation = "23503";
    private const int MaxLimitMovimientos = 200;

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
