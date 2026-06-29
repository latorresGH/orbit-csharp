using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Aderezos;
using OrbIT.Api.MultiTenancy;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de aderezos/salsas + precios y consumos por categoría + ajustes de stock, scopeado por negocio
/// (tenant) vía los Global Query Filters del <c>OrbitDbContext</c>.
///
/// Roles: escritura ADMIN-only; lectura de detalle ADMIN/TRABAJADOR; los listados del menú
/// (<see cref="GetAll"/> y <see cref="GetByCategoriaProducto"/>) son públicos con resolución de tenant
/// por <c>?negocio=slug</c> (ver <see cref="AllowAnonymousWithTenantAttribute"/>).
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Precio/consumo por categoría:</b> NestJS tuvo que agregar a mano (auditoría de seguridad)
///   el chequeo de que aderezo y categoría pertenezcan al tenant. Acá ese chequeo es <i>estructural</i>:
///   el query filter hace que un id de otro negocio devuelva <c>null</c> → 404, sin validación manual.</item>
///   <item><b>Borrado:</b> las FK <c>aderezoId</c> de precio/consumo/categoría son <c>ON DELETE CASCADE</c>
///   reales, así que un único <c>Remove(aderezo)</c> limpia las tres hijas (NestJS las borraba a mano).</item>
///   <item><b>Categorías:</b> los <c>categoriaIds</c> de create/update se validan contra el tenant
///   (estructural, vía el query filter) para no estampar vínculos a categorías de otro negocio.</item>
///   <item><b>Duplicado de nombre:</b> 409 Conflict (consistencia con el resto del proyecto), no el 400
///   que devolvía NestJS.</item>
/// </list>
/// </summary>
[ApiController]
[Route("aderezos")]
[Authorize]
public sealed class AderezoController : ControllerBase
{
    // Código SQLSTATE de violación de unicidad en PostgreSQL (backstop del pre-chequeo de nombre).
    private const string UniqueViolation = "23505";
    private const string TipoAjusteManual = "AJUSTE_MANUAL";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public AderezoController(OrbitDbContext db, ITenantProvider tenant)
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
        [FromQuery] bool soloDisponibles = false)
    {
        // El query filter ya scopea al negocio resuelto (claim o slug). Sólo agregamos los filtros
        // funcionales del listado y proyectamos directo al DTO (AsNoTracking + projection).
        var query = _db.Aderezos.AsNoTracking().AsQueryable();
        if (!incluirInactivos)
        {
            query = query.Where(a => a.Activo);
        }
        if (soloDisponibles)
        {
            query = query.Where(a => a.StockActual > 0);
        }

        var aderezos = await ProjectToResponse(query.OrderBy(a => a.Nombre)).ToListAsync();
        return Ok(aderezos);
    }

    [HttpGet("por-categoria-producto/{categoriaId}")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetByCategoriaProducto(string categoriaId)
    {
        // Aderezos activos con stock que sean globales O estén explícitamente vinculados a la categoría.
        var query = _db.Aderezos.AsNoTracking()
            .Where(a => a.Activo
                        && a.StockActual > 0
                        && (a.EsGlobal || a.AderezoCategoria.Any(ac => ac.CategoriaId == categoriaId)))
            .OrderBy(a => a.Nombre);

        var aderezos = await ProjectToResponse(query).ToListAsync();
        return Ok(aderezos);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura de detalle: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("{id}", Name = nameof(GetAderezoById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetAderezoById(string id)
    {
        var aderezo = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == id))
            .FirstOrDefaultAsync();
        return aderezo is null ? NotFound() : Ok(aderezo);
    }

    [HttpGet("{id}/consumo/{categoriaId}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetConsumoPorCategoria(string id, string categoriaId)
    {
        // El query filter valida la pertenencia del aderezo al tenant.
        if (!await _db.Aderezos.AnyAsync(a => a.Id == id))
        {
            return NotFound(new { message = "Aderezo no encontrado" });
        }

        var consumo = await _db.AderezoConsumos
            .Where(c => c.AderezoId == id && c.CategoriaId == categoriaId)
            .Select(c => (double?)c.CantidadConsumo)
            .FirstOrDefaultAsync();

        // Paridad con NestJS: sin registro de consumo, devuelve 0.
        return Ok(consumo ?? 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateAderezoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var nombre = request.Nombre.Trim();
        if (await _db.Aderezos.AnyAsync(a => a.Nombre == nombre))
        {
            return NombreDuplicado(nombre);
        }

        // Validación estructural de las categorías contra el tenant (no estampar vínculos cross-tenant).
        var (categoriasOk, categoriasError, categoriaIds) = await ValidarCategoriaIds(request.CategoriaIds);
        if (!categoriasOk)
        {
            return categoriasError!;
        }

        var aderezo = new Aderezo
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            StockActual = request.StockActual,
            UnidadMedida = request.UnidadMedida,
            Precio = request.Precio,
            EsPremium = request.EsPremium,
            EsGlobal = request.EsGlobal,
            Activo = true,
            // Stamping temporal del tenant (idéntico a Categorias/ToppingGrupo, hasta el override de SaveChanges).
            NegocioId = negocioId,
        };
        _db.Aderezos.Add(aderezo);

        foreach (var catId in categoriaIds)
        {
            _db.AderezoCategoria.Add(new AderezoCategorium
            {
                Id = Guid.NewGuid().ToString(),
                AderezoId = aderezo.Id,
                CategoriaId = catId,
                NegocioId = negocioId,
            });
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Backstop: dos creates concurrentes pueden pasar ambos el pre-chequeo y chocar el índice
            // único (nombre, negocioId). Lo devolvemos como 409 igual.
            return NombreDuplicado(nombre);
        }

        var response = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == aderezo.Id))
            .FirstAsync();
        return CreatedAtAction(nameof(GetAderezoById), new { id = aderezo.Id }, response);
    }

    [HttpPost("precio-categoria")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetPrecioCategoria([FromBody] SetPrecioCategoriaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        // El bug que NestJS arregló a mano en la auditoría (validar que aderezo y categoría sean del
        // tenant) acá es estructural: el query filter hace que un id ajeno devuelva null → 404.
        if (!await _db.Aderezos.AnyAsync(a => a.Id == request.AderezoId))
        {
            return NotFound(new { message = "Aderezo no encontrado" });
        }
        if (!await _db.Categoria.AnyAsync(c => c.Id == request.CategoriaId))
        {
            return NotFound(new { message = "Categoría no encontrada" });
        }

        // Upsert por (aderezoId, categoriaId): EF no tiene upsert nativo → read + update-or-add.
        var precio = await _db.AderezoPrecios
            .FirstOrDefaultAsync(p => p.AderezoId == request.AderezoId && p.CategoriaId == request.CategoriaId);
        if (precio is null)
        {
            precio = new AderezoPrecio
            {
                Id = Guid.NewGuid().ToString(),
                AderezoId = request.AderezoId,
                CategoriaId = request.CategoriaId,
                Precio = request.Precio,
                NegocioId = negocioId,
            };
            _db.AderezoPrecios.Add(precio);
        }
        else
        {
            precio.Precio = request.Precio;
        }

        await _db.SaveChangesAsync();

        var categoriaNombre = await _db.Categoria
            .Where(c => c.Id == precio.CategoriaId)
            .Select(c => c.Nombre)
            .FirstOrDefaultAsync();
        return Ok(new AderezoPrecioResponse(precio.Id, precio.CategoriaId, categoriaNombre, precio.Precio));
    }

    [HttpPost("consumo-categoria")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetConsumoCategoria([FromBody] SetConsumoCategoriaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        if (!await _db.Aderezos.AnyAsync(a => a.Id == request.AderezoId))
        {
            return NotFound(new { message = "Aderezo no encontrado" });
        }
        if (!await _db.Categoria.AnyAsync(c => c.Id == request.CategoriaId))
        {
            return NotFound(new { message = "Categoría no encontrada" });
        }

        var consumo = await _db.AderezoConsumos
            .FirstOrDefaultAsync(c => c.AderezoId == request.AderezoId && c.CategoriaId == request.CategoriaId);
        if (consumo is null)
        {
            consumo = new AderezoConsumo
            {
                Id = Guid.NewGuid().ToString(),
                AderezoId = request.AderezoId,
                CategoriaId = request.CategoriaId,
                CantidadConsumo = request.CantidadConsumo,
                NegocioId = negocioId,
            };
            _db.AderezoConsumos.Add(consumo);
        }
        else
        {
            consumo.CantidadConsumo = request.CantidadConsumo;
        }

        await _db.SaveChangesAsync();

        var categoriaNombre = await _db.Categoria
            .Where(c => c.Id == consumo.CategoriaId)
            .Select(c => c.Nombre)
            .FirstOrDefaultAsync();
        return Ok(new AderezoConsumoResponse(consumo.Id, consumo.CategoriaId, categoriaNombre, consumo.CantidadConsumo));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateAderezoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var aderezo = await _db.Aderezos.FirstOrDefaultAsync(a => a.Id == id);
        if (aderezo is null)
        {
            return NotFound();
        }

        if (request.Nombre is not null)
        {
            var nombre = request.Nombre.Trim();
            if (!string.Equals(aderezo.Nombre, nombre, StringComparison.Ordinal)
                && await _db.Aderezos.AnyAsync(a => a.Nombre == nombre && a.Id != id))
            {
                return NombreDuplicado(nombre);
            }
            aderezo.Nombre = nombre;
        }

        // Ajuste de stock: registramos el movimiento sólo si el valor efectivamente cambia.
        double? stockAntes = null;
        if (request.StockActual is { } nuevoStock)
        {
            if (nuevoStock != aderezo.StockActual)
            {
                stockAntes = aderezo.StockActual;
            }
            aderezo.StockActual = nuevoStock;
        }

        if (request.Activo is { } activo) aderezo.Activo = activo;
        if (request.UnidadMedida is { } unidad) aderezo.UnidadMedida = unidad;
        if (request.EsGlobal is { } esGlobal) aderezo.EsGlobal = esGlobal;
        if (request.Precio is { } precio) aderezo.Precio = precio;
        if (request.EsPremium is { } esPremium) aderezo.EsPremium = esPremium;

        // Reemplazo total del set de categorías si la lista viene presente (aunque vacía).
        if (request.CategoriaIds is not null)
        {
            var (categoriasOk, categoriasError, categoriaIds) = await ValidarCategoriaIds(request.CategoriaIds);
            if (!categoriasOk)
            {
                return categoriasError!;
            }

            var existentes = await _db.AderezoCategoria.Where(ac => ac.AderezoId == id).ToListAsync();
            _db.AderezoCategoria.RemoveRange(existentes);
            foreach (var catId in categoriaIds)
            {
                _db.AderezoCategoria.Add(new AderezoCategorium
                {
                    Id = Guid.NewGuid().ToString(),
                    AderezoId = id,
                    CategoriaId = catId,
                    NegocioId = negocioId,
                });
            }
        }

        if (stockAntes is { } antes)
        {
            _db.StockMovimientos.Add(NuevoMovimiento(
                id, negocioId,
                cantidad: aderezo.StockActual - antes,
                stockAntes: antes,
                stockDespues: aderezo.StockActual,
                motivo: "Edición manual desde admin"));
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(request.Nombre!.Trim());
        }

        var response = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/activo")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetActivo(string id, [FromBody] SetActivoRequest request)
    {
        var aderezo = await _db.Aderezos.FirstOrDefaultAsync(a => a.Id == id);
        if (aderezo is null)
        {
            return NotFound();
        }

        aderezo.Activo = request.Activo;
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/sumar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SumarStock(string id, [FromBody] AjusteStockRequest request)
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

        var stockAntes = await _db.Aderezos.Where(a => a.Id == id)
            .Select(a => (double?)a.StockActual)
            .FirstOrDefaultAsync();
        if (stockAntes is null)
        {
            return NotFound(new { message = "Aderezo no encontrado" });
        }

        // Incremento atómico (respeta el query filter). Evita perder updates concurrentes.
        await _db.Aderezos.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.StockActual, a => a.StockActual + request.Cantidad));

        var stockDespues = stockAntes.Value + request.Cantidad;
        _db.StockMovimientos.Add(NuevoMovimiento(
            id, negocioId,
            cantidad: request.Cantidad,
            stockAntes: stockAntes.Value,
            stockDespues: stockDespues,
            motivo: "Reposición manual de aderezo"));
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/descontar")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DescontarStock(string id, [FromBody] AjusteStockRequest request)
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

        var stockAntes = await _db.Aderezos.Where(a => a.Id == id)
            .Select(a => (double?)a.StockActual)
            .FirstOrDefaultAsync();
        if (stockAntes is null)
        {
            return NotFound(new { message = "Aderezo no encontrado" });
        }

        // Decremento atómico con guarda de stock: el UPDATE sólo afecta filas con stock suficiente.
        // 0 filas afectadas (tras confirmar que el aderezo existe) ⇒ stock insuficiente.
        var filas = await _db.Aderezos
            .Where(a => a.Id == id && a.StockActual >= request.Cantidad)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.StockActual, a => a.StockActual - request.Cantidad));
        if (filas == 0)
        {
            return BadRequest(new { message = "Stock insuficiente" });
        }

        var stockDespues = stockAntes.Value - request.Cantidad;
        _db.StockMovimientos.Add(NuevoMovimiento(
            id, negocioId,
            cantidad: -request.Cantidad,
            stockAntes: stockAntes.Value,
            stockDespues: stockDespues,
            motivo: "Descuento manual de aderezo"));
        await _db.SaveChangesAsync();

        var response = await ProjectToResponse(_db.Aderezos.AsNoTracking().Where(a => a.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var aderezo = await _db.Aderezos.FirstOrDefaultAsync(a => a.Id == id);
        if (aderezo is null)
        {
            return NotFound();
        }

        // Las FK aderezoId de precio/consumo/categoría son ON DELETE CASCADE: la base limpia las tres
        // hijas en el mismo borrado (a diferencia de NestJS, que las borraba a mano por una limitación
        // de Prisma). Los StockMovimiento NO tienen FK al aderezo: quedan con su aderezoId histórico.
        _db.Aderezos.Remove(aderezo);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Valida que todos los <paramref name="categoriaIds"/> existan en el negocio activo (el query
    /// filter ya excluye los de otro tenant). Devuelve los ids distintos si están todos OK, o un
    /// 400 si alguno no pertenece al negocio. <c>null</c>/vacío ⇒ OK sin ids.
    /// </summary>
    private async Task<(bool Ok, IActionResult? Error, List<string> Ids)> ValidarCategoriaIds(
        List<string>? categoriaIds)
    {
        var distinct = categoriaIds?.Distinct().ToList() ?? new List<string>();
        if (distinct.Count == 0)
        {
            return (true, null, distinct);
        }

        var validos = await _db.Categoria
            .Where(c => distinct.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();
        if (validos.Count != distinct.Count)
        {
            return (false, BadRequest(new { message = "Alguna categoría no existe o no pertenece al negocio." }), distinct);
        }

        return (true, null, distinct);
    }

    private StockMovimiento NuevoMovimiento(
        string aderezoId, string negocioId,
        double cantidad, double stockAntes, double stockDespues, string motivo) => new()
        {
            Id = Guid.NewGuid().ToString(),
            AderezoId = aderezoId,
            NegocioId = negocioId,
            Tipo = TipoAjusteManual,
            Cantidad = cantidad,
            StockAntes = stockAntes,
            StockDespues = stockDespues,
            Motivo = motivo,
            UserId = User.FindFirst("sub")?.Value,
            // CreatedAt se deja sin asignar: la columna tiene DEFAULT CURRENT_TIMESTAMP y EF lo trata
            // como valor generado en el INSERT (el sentinel DateTime.MinValue dispara el default de DB).
        };

    private static IQueryable<AderezoResponse> ProjectToResponse(IQueryable<Aderezo> query) =>
        query.Select(a => new AderezoResponse(
            a.Id,
            a.Nombre,
            a.UnidadMedida,
            a.Activo,
            a.StockActual,
            a.EsGlobal,
            a.EsPremium,
            a.Precio,
            a.AderezoCategoria
                .Select(ac => new AderezoCategoriaResponse(ac.Id, ac.CategoriaId, ac.Categoria.Nombre))
                .ToList(),
            a.AderezoPrecios
                .Select(ap => new AderezoPrecioResponse(ap.Id, ap.CategoriaId, ap.Categoria.Nombre, ap.Precio))
                .ToList(),
            a.AderezoConsumos
                .Select(co => new AderezoConsumoResponse(co.Id, co.CategoriaId, co.Categoria.Nombre, co.CantidadConsumo))
                .ToList()));

    private ConflictObjectResult NombreDuplicado(string nombre) =>
        Conflict(new { message = $"Ya existe un aderezo con el nombre '{nombre}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
