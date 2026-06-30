using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Extras;
using OrbIT.Api.MultiTenancy;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de extras/toppings + precios y consumos por categoría + ajustes de stock. Módulo gemelo de
/// <see cref="AderezoController"/> (mismo patrón consolidado), con su particularidad: el Extra puede
/// pertenecer a un <c>ToppingGrupo</c> (FK nullable, gratuidad a nivel grupo) y puede estar respaldado
/// por un <c>Insumo</c> (FK nullable) del que descuenta stock.
///
/// Roles: escritura ADMIN-only; lectura de detalle ADMIN/TRABAJADOR; los listados del menú
/// (<see cref="GetAll"/> y <see cref="GetByCategoriaProducto"/>) son públicos con resolución de tenant
/// por <c>?negocio=slug</c> (<see cref="AllowAnonymousWithTenantAttribute"/>).
///
/// Paridad y mejoras respecto al NestJS de producción (idénticas a Aderezo salvo lo del insumo):
/// <list type="bullet">
///   <item><b>Precio/consumo por categoría:</b> el chequeo de tenant que NestJS agregó a mano en la
///   auditoría acá es estructural (query filter → id ajeno = null = 404).</item>
///   <item><b>Borrado:</b> FK <c>extraId</c> de precio/consumo/categoría son ON DELETE CASCADE; un solo
///   <c>Remove</c> limpia las hijas.</item>
///   <item><b>Validación estructural de tenant</b> en <c>categoriaIds</c>, las keys de <c>consumos</c>,
///   <c>toppingGrupoId</c> e <c>insumoId</c> (NestJS no scopeaba insumo ni grupo por tenant).</item>
///   <item><b>Stock insumo-backed:</b> si el Extra tiene <c>InsumoId</c>, sumar/descontar operan sobre el
///   Insumo (tenant-scoped) y registran el movimiento con <c>insumoId</c>; si no, sobre el Extra.</item>
///   <item><b>Duplicado de nombre:</b> 409 + backstop (NestJS no pre-chequeaba y reventaría el índice único).</item>
/// </list>
/// </summary>
[ApiController]
[Route("extras")]
[Authorize]
public sealed class ExtraController : ControllerBase
{
    private const string UniqueViolation = "23505";
    private const string TipoAjusteManual = "AJUSTE_MANUAL";
    private const string CategoriaDefault = "TOPPINGS";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public ExtraController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura pública (menú): tenant por claim (si hay sesión) o por ?negocio=slug.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool incluirInactivos = false,
        [FromQuery] bool soloDisponibles = false,
        [FromQuery] string? categoria = null)
    {
        var query = _db.Extras.AsNoTracking().AsQueryable();
        if (!incluirInactivos)
        {
            query = query.Where(e => e.Activo);
        }
        if (soloDisponibles)
        {
            query = query.Where(e => e.StockActual > 0);
        }
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var cat = NormCategoria(categoria);
            query = query.Where(e => e.Categoria == cat);
        }

        var extras = await ProjectToResponse(query.OrderBy(e => e.Categoria).ThenBy(e => e.Nombre)).ToListAsync();
        return Ok(extras);
    }

    [HttpGet("por-categoria-producto/{categoriaId}")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetByCategoriaProducto(string categoriaId)
    {
        var query = _db.Extras.AsNoTracking()
            .Where(e => e.Activo
                        && e.StockActual > 0
                        && (e.EsGlobal || e.ExtraCategoria.Any(ec => ec.CategoriaId == categoriaId)))
            .OrderBy(e => e.Nombre);

        var extras = await ProjectToResponse(query).ToListAsync();
        return Ok(extras);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura de detalle: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("{id}", Name = nameof(GetExtraById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetExtraById(string id)
    {
        var extra = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == id))
            .FirstOrDefaultAsync();
        return extra is null ? NotFound() : Ok(extra);
    }

    [HttpGet("{id}/consumo/{categoriaId}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetConsumoPorCategoria(string id, string categoriaId)
    {
        if (!await _db.Extras.AnyAsync(e => e.Id == id))
        {
            return NotFound(new { message = "Extra no encontrado" });
        }

        var consumo = await _db.ExtraConsumos
            .Where(c => c.ExtraId == id && c.CategoriaId == categoriaId)
            .Select(c => (double?)c.CantidadConsumo)
            .FirstOrDefaultAsync();

        return Ok(consumo ?? 0);
    }

    [HttpGet("{extraId}/precio/{categoriaId}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetPrecioPorCategoria(string extraId, string categoriaId)
    {
        // El query filter valida la pertenencia del extra al tenant.
        var precioBase = await _db.Extras
            .Where(e => e.Id == extraId)
            .Select(e => (double?)e.Precio)
            .FirstOrDefaultAsync();
        if (precioBase is null)
        {
            return NotFound(new { message = "Extra no encontrado" });
        }

        // Precio específico de la categoría si existe; si no, cae al precio base del extra.
        var especifico = await _db.ExtraPrecios
            .Where(p => p.ExtraId == extraId && p.CategoriaId == categoriaId)
            .Select(p => (double?)p.Precio)
            .FirstOrDefaultAsync();

        return Ok(especifico ?? precioBase.Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateExtraRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var nombre = request.Nombre.Trim();
        if (nombre.Length == 0)
        {
            return BadRequest(new { message = "nombre es obligatorio" });
        }
        if (await _db.Extras.AnyAsync(e => e.Nombre == nombre))
        {
            return NombreDuplicado(nombre);
        }

        // Validaciones estructurales de tenant (insumo, grupo, categorías, keys de consumos).
        if (await ValidarInsumo(request.InsumoId) is { } insumoError)
        {
            return insumoError;
        }
        if (await ValidarToppingGrupo(request.ToppingGrupoId) is { } grupoError)
        {
            return grupoError;
        }
        var categoriasReferidas = (request.CategoriaIds ?? Enumerable.Empty<string>())
            .Concat(request.Consumos?.Keys ?? Enumerable.Empty<string>());
        if (await ValidarCategoriasDelTenant(categoriasReferidas) is { } catError)
        {
            return catError;
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var extra = new Extra
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            Precio = request.Precio,
            StockActual = request.StockActual,
            Activo = request.Activo,
            Categoria = NormCategoria(request.Categoria),
            UnidadMedida = request.UnidadMedida,
            InsumoId = NullIfBlank(request.InsumoId),
            EsGlobal = request.EsGlobal,
            EsPremium = request.EsPremium,
            ToppingGrupoId = NullIfBlank(request.ToppingGrupoId),
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Extras.Add(extra);

        foreach (var catId in DistinctIds(request.CategoriaIds))
        {
            _db.ExtraCategoria.Add(new ExtraCategorium
            {
                Id = Guid.NewGuid().ToString(),
                ExtraId = extra.Id,
                CategoriaId = catId,
                NegocioId = negocioId,
            });
        }

        foreach (var (catId, cantidad) in ConsumosValidos(request.Consumos))
        {
            _db.ExtraConsumos.Add(new ExtraConsumo
            {
                Id = Guid.NewGuid().ToString(),
                ExtraId = extra.Id,
                CategoriaId = catId,
                CantidadConsumo = cantidad,
                NegocioId = negocioId,
            });
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(nombre);
        }

        var response = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == extra.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetExtraById), new { id = extra.Id }, response);
    }

    [HttpPost("precio-categoria")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetPrecioCategoria([FromBody] SetExtraPrecioCategoriaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        // Estructural: extra y categoría deben ser del tenant (query filter → ajeno = null = 404).
        if (!await _db.Extras.AnyAsync(e => e.Id == request.ExtraId))
        {
            return NotFound(new { message = "Extra no encontrado" });
        }
        if (!await _db.Categoria.AnyAsync(c => c.Id == request.CategoriaId))
        {
            return NotFound(new { message = "Categoría no encontrada" });
        }

        var precio = await _db.ExtraPrecios
            .FirstOrDefaultAsync(p => p.ExtraId == request.ExtraId && p.CategoriaId == request.CategoriaId);
        if (precio is null)
        {
            precio = new ExtraPrecio
            {
                Id = Guid.NewGuid().ToString(),
                ExtraId = request.ExtraId,
                CategoriaId = request.CategoriaId,
                Precio = request.Precio,
                NegocioId = negocioId,
            };
            _db.ExtraPrecios.Add(precio);
        }
        else
        {
            precio.Precio = request.Precio;
        }

        await _db.SaveChangesAsync();

        var categoriaNombre = await _db.Categoria
            .Where(c => c.Id == precio.CategoriaId).Select(c => c.Nombre).FirstOrDefaultAsync();
        return Ok(new ExtraPrecioResponse(precio.Id, precio.CategoriaId, categoriaNombre, precio.Precio));
    }

    [HttpPost("consumo-categoria")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetConsumoCategoria([FromBody] SetExtraConsumoCategoriaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        if (!await _db.Extras.AnyAsync(e => e.Id == request.ExtraId))
        {
            return NotFound(new { message = "Extra no encontrado" });
        }
        if (!await _db.Categoria.AnyAsync(c => c.Id == request.CategoriaId))
        {
            return NotFound(new { message = "Categoría no encontrada" });
        }

        var consumo = await _db.ExtraConsumos
            .FirstOrDefaultAsync(c => c.ExtraId == request.ExtraId && c.CategoriaId == request.CategoriaId);
        if (consumo is null)
        {
            consumo = new ExtraConsumo
            {
                Id = Guid.NewGuid().ToString(),
                ExtraId = request.ExtraId,
                CategoriaId = request.CategoriaId,
                CantidadConsumo = request.CantidadConsumo,
                NegocioId = negocioId,
            };
            _db.ExtraConsumos.Add(consumo);
        }
        else
        {
            consumo.CantidadConsumo = request.CantidadConsumo;
        }

        await _db.SaveChangesAsync();

        var categoriaNombre = await _db.Categoria
            .Where(c => c.Id == consumo.CategoriaId).Select(c => c.Nombre).FirstOrDefaultAsync();
        return Ok(new ExtraConsumoResponse(consumo.Id, consumo.CategoriaId, categoriaNombre, consumo.CantidadConsumo));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateExtraRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var extra = await _db.Extras.FirstOrDefaultAsync(e => e.Id == id);
        if (extra is null)
        {
            return NotFound();
        }

        if (request.Nombre is not null)
        {
            var nombre = request.Nombre.Trim();
            if (!string.Equals(extra.Nombre, nombre, StringComparison.Ordinal)
                && await _db.Extras.AnyAsync(e => e.Nombre == nombre && e.Id != id))
            {
                return NombreDuplicado(nombre);
            }
            extra.Nombre = nombre;
        }

        // insumoId / toppingGrupoId: null = no tocar; "" = desvincular; valor = vincular (validado).
        if (request.InsumoId is not null)
        {
            var insumoId = NullIfBlank(request.InsumoId);
            if (await ValidarInsumo(insumoId) is { } insumoError)
            {
                return insumoError;
            }
            extra.InsumoId = insumoId;
        }
        if (request.ToppingGrupoId is not null)
        {
            var grupoId = NullIfBlank(request.ToppingGrupoId);
            if (await ValidarToppingGrupo(grupoId) is { } grupoError)
            {
                return grupoError;
            }
            extra.ToppingGrupoId = grupoId;
        }

        double? stockAntes = null;
        if (request.StockActual is { } nuevoStock)
        {
            if (nuevoStock != extra.StockActual)
            {
                stockAntes = extra.StockActual;
            }
            extra.StockActual = nuevoStock;
        }

        if (request.Precio is { } precio) extra.Precio = precio;
        if (request.Activo is { } activo) extra.Activo = activo;
        if (request.Categoria is not null) extra.Categoria = NormCategoria(request.Categoria);
        if (request.UnidadMedida is { } unidad) extra.UnidadMedida = unidad;
        if (request.EsGlobal is { } esGlobal) extra.EsGlobal = esGlobal;
        if (request.EsPremium is { } esPremium) extra.EsPremium = esPremium;

        // Reemplazo total de categorías y/o consumos si la lista/mapa viene presente (aunque vacío).
        var categoriasReferidas = Enumerable.Empty<string>();
        if (request.CategoriaIds is not null)
        {
            categoriasReferidas = categoriasReferidas.Concat(request.CategoriaIds);
        }
        if (request.Consumos is not null)
        {
            categoriasReferidas = categoriasReferidas.Concat(request.Consumos.Keys);
        }
        if (await ValidarCategoriasDelTenant(categoriasReferidas) is { } catError)
        {
            return catError;
        }

        if (request.CategoriaIds is not null)
        {
            var existentes = await _db.ExtraCategoria.Where(ec => ec.ExtraId == id).ToListAsync();
            _db.ExtraCategoria.RemoveRange(existentes);
            foreach (var catId in DistinctIds(request.CategoriaIds))
            {
                _db.ExtraCategoria.Add(new ExtraCategorium
                {
                    Id = Guid.NewGuid().ToString(),
                    ExtraId = id,
                    CategoriaId = catId,
                    NegocioId = negocioId,
                });
            }
        }

        if (request.Consumos is not null)
        {
            var existentes = await _db.ExtraConsumos.Where(c => c.ExtraId == id).ToListAsync();
            _db.ExtraConsumos.RemoveRange(existentes);
            foreach (var (catId, cantidad) in ConsumosValidos(request.Consumos))
            {
                _db.ExtraConsumos.Add(new ExtraConsumo
                {
                    Id = Guid.NewGuid().ToString(),
                    ExtraId = id,
                    CategoriaId = catId,
                    CantidadConsumo = cantidad,
                    NegocioId = negocioId,
                });
            }
        }

        if (stockAntes is { } antes)
        {
            // El ajuste de stock vía PATCH siempre se contabiliza sobre el propio Extra (paridad con
            // NestJS: el ruteo por insumo es exclusivo de los endpoints sumar/descontar).
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: extra.StockActual - antes,
                stockAntes: antes,
                stockDespues: extra.StockActual,
                motivo: "Edición manual desde admin",
                extraId: id));
        }

        extra.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(request.Nombre!.Trim());
        }

        var response = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/activo")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetActivo(string id, [FromBody] ToggleExtraActivoRequest request)
    {
        var extra = await _db.Extras.FirstOrDefaultAsync(e => e.Id == id);
        if (extra is null)
        {
            return NotFound();
        }

        extra.Activo = request.Activo;
        extra.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/sumar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SumarStock(string id, [FromBody] ExtraStockMovRequest request)
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

        var extra = await _db.Extras.Where(e => e.Id == id)
            .Select(e => new { e.InsumoId, e.StockActual })
            .FirstOrDefaultAsync();
        if (extra is null)
        {
            return NotFound(new { message = "Extra no encontrado" });
        }

        if (extra.InsumoId is not null)
        {
            // Stock respaldado por insumo: el movimiento opera sobre el Insumo (tenant-scoped).
            var stockAntes = await _db.Insumos.Where(i => i.Id == extra.InsumoId)
                .Select(i => (double?)i.StockActual).FirstOrDefaultAsync() ?? 0;
            await _db.Insumos.Where(i => i.Id == extra.InsumoId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual + request.Cantidad));
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: request.Cantidad,
                stockAntes: stockAntes,
                stockDespues: stockAntes + request.Cantidad,
                motivo: request.Motivo ?? "Reposición de extra (vía insumo)",
                insumoId: extra.InsumoId));
        }
        else
        {
            var stockAntes = extra.StockActual;
            await _db.Extras.Where(e => e.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StockActual, e => e.StockActual + request.Cantidad));
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: request.Cantidad,
                stockAntes: stockAntes,
                stockDespues: stockAntes + request.Cantidad,
                motivo: request.Motivo ?? "Reposición manual de extra",
                extraId: id));
        }

        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/descontar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DescontarStock(string id, [FromBody] ExtraStockMovRequest request)
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

        var extra = await _db.Extras.Where(e => e.Id == id)
            .Select(e => new { e.InsumoId, e.StockActual })
            .FirstOrDefaultAsync();
        if (extra is null)
        {
            return NotFound(new { message = "Extra no encontrado" });
        }

        if (extra.InsumoId is not null)
        {
            var stockAntes = await _db.Insumos.Where(i => i.Id == extra.InsumoId)
                .Select(i => (double?)i.StockActual).FirstOrDefaultAsync() ?? 0;
            var filas = await _db.Insumos.Where(i => i.Id == extra.InsumoId && i.StockActual >= request.Cantidad)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual - request.Cantidad));
            if (filas == 0)
            {
                return BadRequest(new { message = "Stock insuficiente en el insumo asociado" });
            }
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: -request.Cantidad,
                stockAntes: stockAntes,
                stockDespues: stockAntes - request.Cantidad,
                motivo: request.Motivo ?? "Descuento manual de extra (vía insumo)",
                insumoId: extra.InsumoId));
        }
        else
        {
            var stockAntes = extra.StockActual;
            var filas = await _db.Extras.Where(e => e.Id == id && e.StockActual >= request.Cantidad)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StockActual, e => e.StockActual - request.Cantidad));
            if (filas == 0)
            {
                return BadRequest(new { message = "Stock insuficiente" });
            }
            _db.StockMovimientos.Add(NuevoMovimiento(
                negocioId,
                cantidad: -request.Cantidad,
                stockAntes: stockAntes,
                stockDespues: stockAntes - request.Cantidad,
                motivo: request.Motivo ?? "Descuento manual de extra",
                extraId: id));
        }

        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Extras.AsNoTracking().Where(e => e.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var extra = await _db.Extras.FirstOrDefaultAsync(e => e.Id == id);
        if (extra is null)
        {
            return NotFound();
        }

        // FK extraId de precio/consumo/categoría son ON DELETE CASCADE: la base limpia las hijas.
        _db.Extras.Remove(extra);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Normaliza la categoría libre: trim + MAYÚSCULAS; vacío ⇒ 'TOPPINGS' (paridad NestJS).</summary>
    private static string NormCategoria(string? categoria)
    {
        var v = (categoria ?? CategoriaDefault).Trim();
        return v.Length == 0 ? CategoriaDefault : v.ToUpperInvariant();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> DistinctIds(List<string>? ids) =>
        ids?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList() ?? new List<string>();

    private static IEnumerable<(string CategoriaId, double Cantidad)> ConsumosValidos(Dictionary<string, double>? consumos) =>
        consumos is null
            ? Enumerable.Empty<(string, double)>()
            : consumos.Where(kv => kv.Value > 0 && !string.IsNullOrWhiteSpace(kv.Key))
                      .Select(kv => (kv.Key, kv.Value));

    /// <summary>Valida que el insumo (si se indica) exista en el negocio activo. <c>null</c> ⇒ OK.</summary>
    private async Task<IActionResult?> ValidarInsumo(string? insumoId)
    {
        if (string.IsNullOrWhiteSpace(insumoId))
        {
            return null;
        }
        return await _db.Insumos.AnyAsync(i => i.Id == insumoId)
            ? null
            : BadRequest(new { message = $"Insumo con ID {insumoId} no encontrado." });
    }

    /// <summary>Valida que el grupo de toppings (si se indica) sea del negocio activo. <c>null</c> ⇒ OK.</summary>
    private async Task<IActionResult?> ValidarToppingGrupo(string? toppingGrupoId)
    {
        if (string.IsNullOrWhiteSpace(toppingGrupoId))
        {
            return null;
        }
        return await _db.ToppingGrupos.AnyAsync(g => g.Id == toppingGrupoId)
            ? null
            : BadRequest(new { message = "El grupo de toppings no existe o no pertenece al negocio." });
    }

    /// <summary>Valida que todas las categorías referidas existan en el negocio activo. Vacío ⇒ OK.</summary>
    private async Task<IActionResult?> ValidarCategoriasDelTenant(IEnumerable<string> categoriaIds)
    {
        var distinct = categoriaIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return null;
        }

        var validos = await _db.Categoria
            .Where(c => distinct.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        return validos.Count == distinct.Count
            ? null
            : BadRequest(new { message = "Alguna categoría no existe o no pertenece al negocio." });
    }

    private StockMovimiento NuevoMovimiento(
        string negocioId, double cantidad, double stockAntes, double stockDespues, string motivo,
        string? extraId = null, string? insumoId = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            ExtraId = extraId,
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

    private static IQueryable<ExtraResponse> ProjectToResponse(IQueryable<Extra> query) =>
        query.Select(e => new ExtraResponse(
            e.Id,
            e.Nombre,
            e.UnidadMedida,
            e.Precio,
            e.StockActual,
            e.Activo,
            e.Categoria,
            e.EsGlobal,
            e.EsPremium,
            e.InsumoId,
            e.ToppingGrupoId,
            e.CreatedAt,
            e.UpdatedAt,
            e.ExtraCategoria
                .Select(ec => new ExtraCategoriaResponse(ec.Id, ec.CategoriaId, ec.Categoria.Nombre))
                .ToList(),
            e.ExtraPrecios
                .Select(ep => new ExtraPrecioResponse(ep.Id, ep.CategoriaId, ep.Categoria.Nombre, ep.Precio))
                .ToList(),
            e.ExtraConsumos
                .Select(co => new ExtraConsumoResponse(co.Id, co.CategoriaId, co.Categoria.Nombre, co.CantidadConsumo))
                .ToList()));

    private ConflictObjectResult NombreDuplicado(string nombre) =>
        Conflict(new { message = $"Ya existe un extra con el nombre '{nombre}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
