using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Productos;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Audit;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de productos + menú público + receta (insumos). Mismo patrón consolidado que
/// <see cref="ExtraController"/> y <see cref="AderezoController"/>, con sus particularidades:
/// la receta (insumo × cantidad) y la auditoría de cambio de precio.
///
/// Paridad y mejoras respecto al NestJS de producción ("optimizar, no calcar"):
/// <list type="bullet">
///   <item><b>Menú público (<c>?basico=true</c>):</b> projection directa a DTO con <c>TieneReceta</c>
///   como EXISTS correlacionado — no se materializan filas de receta ni se exponen insumos (ver
///   memoria: el storefront no usa la receta).</item>
///   <item><b>Hardening (decisión C):</b> el menú COMPLETO (sin <c>basico</c>, que sí trae receta con
///   insumos) exige sesión autenticada. NestJS lo dejaba público; acá la receta no se sirve a anónimos.</item>
///   <item><b>mas-vendidos:</b> el ranking se calcula con un <c>GROUP BY</c> + <c>SUM</c> server-side
///   (no en memoria); sólo el fallback aleatorio baraja en memoria (igual que NestJS).</item>
///   <item><b>categoriaId / insumoId:</b> validación de pertenencia al tenant gratis vía Global Query
///   Filter (id ajeno = no encontrado).</item>
///   <item><b>codigo duplicado:</b> pre-check → 409 antes de reventar la unique constraint (NestJS no
///   pre-chequeaba).</item>
///   <item><b>Auditoría CAMBIO_PRECIO:</b> vía <see cref="IAuditLogService"/> (incluye el userId del
///   claim <c>sub</c>, mejora sobre NestJS que no lo registraba).</item>
/// </list>
///
/// Nota de método HTTP: la actualización es <c>PATCH</c> (no PUT) para mantener el contrato vivo —
/// el NestJS expone <c>@Patch(':id')</c> y el frontend llama <c>api.patch</c>.
/// </summary>
[ApiController]
[Route("productos")]
[Authorize]
public sealed class ProductoController : ControllerBase
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IAuditLogService _audit;

    public ProductoController(OrbitDbContext db, ITenantProvider tenant, IAuditLogService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Listados (menú): públicos con tenant por claim o por ?negocio=slug.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lista productos. <c>basico=true</c> → versión liviana del storefront (sin receta, público).
    /// Sin <c>basico</c> → menú completo con receta e insumos, restringido a usuarios autenticados.
    /// </summary>
    [HttpGet]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool basico = false,
        [FromQuery] bool incluirInactivos = false)
    {
        if (basico)
        {
            // Paridad NestJS (listarBasico): devuelve todos; el front filtra inactivos client-side.
            var basicos = await ProjectBasico(OrdenadoMenu(_db.Productos.AsNoTracking())).ToListAsync();
            return Ok(basicos);
        }

        // Hardening (decisión C): el menú completo trae la receta con insumos → sólo autenticados.
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { message = "Se requiere autenticación para el menú completo." });
        }

        var query = _db.Productos.AsNoTracking().AsQueryable();
        if (!incluirInactivos)
        {
            query = query.Where(p => p.Activo);
        }

        var completos = await ProjectFull(OrdenadoMenu(query)).ToListAsync();
        return Ok(completos);
    }

    /// <summary>
    /// Top 5 productos por unidades vendidas (suma de <c>PedidoDetalle.Cantidad</c>). Si no hay ventas,
    /// devuelve 5 productos activos al azar. Público (no expone receta).
    /// </summary>
    [HttpGet("mas-vendidos")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> GetMasVendidos()
    {
        var ranking = await _db.PedidoDetalles.AsNoTracking()
            .GroupBy(d => d.ProductoId)
            .Select(g => new { ProductoId = g.Key, Total = g.Sum(d => d.Cantidad) })
            .OrderByDescending(x => x.Total)
            .Take(5)
            .ToListAsync();

        if (ranking.Count == 0)
        {
            // Fallback: activos barajados en memoria (tabla chica por tenant; paridad NestJS).
            var activos = await ProjectBasico(_db.Productos.AsNoTracking().Where(p => p.Activo)).ToListAsync();
            Shuffle(activos);
            return Ok(activos.Take(5).ToList());
        }

        var ids = ranking.Select(r => r.ProductoId).ToList();
        var productos = await ProjectBasico(_db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Activo))
            .ToListAsync();

        // Reordenar según el ranking de ventas (Contains no garantiza orden).
        var ordenados = ids
            .Select(id => productos.FirstOrDefault(p => p.Id == id))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
        return Ok(ordenados);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Detalle: cualquier rol autenticado del tenant.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet("{id}", Name = nameof(GetProductoById))]
    [Authorize]
    public async Task<IActionResult> GetProductoById(string id)
    {
        var producto = await ProjectFull(_db.Productos.AsNoTracking().Where(p => p.Id == id))
            .FirstOrDefaultAsync();
        return producto is null ? NotFound(new { message = "Producto no encontrado" }) : Ok(producto);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateProductoRequest request)
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

        // Categoría obligatoria y del tenant (ajena = no encontrada por el query filter → 400, paridad NestJS).
        if (!await _db.Categoria.AnyAsync(c => c.Id == request.CategoriaId))
        {
            return BadRequest(new { message = "Categoría inválida" });
        }

        var codigo = NullIfBlank(request.Codigo);
        if (codigo is not null && await _db.Productos.AnyAsync(p => p.Codigo == codigo))
        {
            return CodigoDuplicado(codigo);
        }

        if (await ValidarInsumos(request.Receta) is { } insumoError)
        {
            return insumoError;
        }

        var producto = new Producto
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = nombre,
            Precio = request.Precio,
            Descripcion = NullIfBlank(request.Descripcion),
            ImagenUrl = NullIfBlank(request.ImagenUrl),
            Codigo = codigo,
            TiempoPreparacionMin = request.TiempoPreparacionMin,
            EsVegetariano = request.EsVegetariano,
            Badge = NullIfBlank(request.Badge),
            PermitirMediaMedia = request.PermitirMediaMedia,
            AceptaSalsas = request.AceptaSalsas,
            AceptaToppings = request.AceptaToppings,
            ToppingGruposCompatibles = DistinctIds(request.ToppingGruposCompatibles),
            CategoriaId = request.CategoriaId,
            Activo = true,
            NegocioId = negocioId,
        };
        _db.Productos.Add(producto);

        foreach (var item in ItemsValidos(request.Receta))
        {
            _db.ProductoReceta.Add(new ProductoRecetum
            {
                Id = Guid.NewGuid().ToString(),
                ProductoId = producto.Id,
                InsumoId = item.InsumoId,
                Cantidad = item.Cantidad,
                NegocioId = negocioId,
            });
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return CodigoDuplicado(codigo ?? string.Empty);
        }

        var response = await ProjectFull(_db.Productos.AsNoTracking().Where(p => p.Id == producto.Id)).FirstAsync();
        return CreatedAtAction(nameof(GetProductoById), new { id = producto.Id }, response);
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateProductoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var producto = await _db.Productos.FirstOrDefaultAsync(p => p.Id == id);
        if (producto is null)
        {
            return NotFound(new { message = "Producto no encontrado" });
        }

        var precioAntes = producto.Precio;

        // categoriaId: null = no tocar; "" = desvincular; valor = vincular (validado contra el tenant).
        if (request.CategoriaId is not null)
        {
            var categoriaId = NullIfBlank(request.CategoriaId);
            if (categoriaId is not null && !await _db.Categoria.AnyAsync(c => c.Id == categoriaId))
            {
                return BadRequest(new { message = "Categoría inválida" });
            }
            producto.CategoriaId = categoriaId;
        }

        if (request.Nombre is not null)
        {
            var nombre = request.Nombre.Trim();
            producto.Nombre = nombre;
        }

        // codigo: null = no tocar; "" = limpiar; valor = setear (pre-check de duplicado).
        if (request.Codigo is not null)
        {
            var codigo = NullIfBlank(request.Codigo);
            if (codigo is not null
                && !string.Equals(producto.Codigo, codigo, StringComparison.Ordinal)
                && await _db.Productos.AnyAsync(p => p.Codigo == codigo && p.Id != id))
            {
                return CodigoDuplicado(codigo);
            }
            producto.Codigo = codigo;
        }

        if (request.Precio is { } precio) producto.Precio = precio;
        if (request.Descripcion is not null) producto.Descripcion = NullIfBlank(request.Descripcion);
        if (request.ImagenUrl is not null) producto.ImagenUrl = NullIfBlank(request.ImagenUrl);
        if (request.TiempoPreparacionMin is { } tiempo) producto.TiempoPreparacionMin = tiempo;
        if (request.EsVegetariano is { } esVeg) producto.EsVegetariano = esVeg;
        if (request.Badge is not null) producto.Badge = NullIfBlank(request.Badge);
        if (request.PermitirMediaMedia is { } mm) producto.PermitirMediaMedia = mm;
        if (request.AceptaSalsas is { } salsas) producto.AceptaSalsas = salsas;
        if (request.AceptaToppings is { } toppings) producto.AceptaToppings = toppings;
        if (request.ToppingGruposCompatibles is not null)
        {
            producto.ToppingGruposCompatibles = DistinctIds(request.ToppingGruposCompatibles);
        }

        // Receta presente (aunque vacía) → reemplazo total; ausente → intacta.
        if (request.Receta is not null)
        {
            if (await ValidarInsumos(request.Receta) is { } insumoError)
            {
                return insumoError;
            }

            var existentes = await _db.ProductoReceta.Where(r => r.ProductoId == id).ToListAsync();
            _db.ProductoReceta.RemoveRange(existentes);
            foreach (var item in ItemsValidos(request.Receta))
            {
                _db.ProductoReceta.Add(new ProductoRecetum
                {
                    Id = Guid.NewGuid().ToString(),
                    ProductoId = id,
                    InsumoId = item.InsumoId,
                    Cantidad = item.Cantidad,
                    NegocioId = negocioId,
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return CodigoDuplicado(producto.Codigo ?? string.Empty);
        }

        // Auditoría de cambio de precio (decisión B): incluye el userId del claim sub.
        if (request.Precio is { } nuevoPrecio && nuevoPrecio != precioAntes)
        {
            await _audit.RegistrarAsync(
                accion: "CAMBIO_PRECIO",
                entidad: "Producto",
                entidadId: id,
                detalle: new { antes = precioAntes, despues = nuevoPrecio },
                negocioId: negocioId,
                usuarioId: User.FindFirst("sub")?.Value);
        }

        var response = await ProjectFull(_db.Productos.AsNoTracking().Where(p => p.Id == id)).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/activo")]
    [Authorize(Roles = "ADMIN")]
    public Task<IActionResult> SetActivo(string id, [FromBody] ToggleProductoActivoRequest request) =>
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
        var producto = await _db.Productos.FirstOrDefaultAsync(p => p.Id == id);
        if (producto is null)
        {
            return NotFound(new { message = "Producto no encontrado" });
        }

        // Bloqueo de borrado si el producto ya fue usado en pedidos (paridad NestJS → baja lógica).
        if (await _db.PedidoDetalles.AnyAsync(d => d.ProductoId == id))
        {
            return Conflict(new
            {
                message = "No se puede borrar un producto que ya fue usado en pedidos. Usá baja lógica (activo=false).",
            });
        }

        // La receta tiene FK Restrict: se borra a mano antes del producto.
        var receta = await _db.ProductoReceta.Where(r => r.ProductoId == id).ToListAsync();
        _db.ProductoReceta.RemoveRange(receta);
        _db.Productos.Remove(producto);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            // OfertaProducto / GrupoOpcion referencian al producto con FK Restrict (verificado).
            return Conflict(new
            {
                message = "No se puede borrar: el producto está referenciado por ofertas o combos.",
            });
        }

        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> CambiarActivo(string id, bool activo)
    {
        var producto = await _db.Productos.FirstOrDefaultAsync(p => p.Id == id);
        if (producto is null)
        {
            return NotFound(new { message = "Producto no encontrado" });
        }

        producto.Activo = activo;
        await _db.SaveChangesAsync();

        var response = await ProjectFull(_db.Productos.AsNoTracking().Where(p => p.Id == id)).FirstAsync();
        return Ok(response);
    }

    private static IQueryable<Producto> OrdenadoMenu(IQueryable<Producto> query) =>
        query.OrderBy(p => p.Categoria!.Orden).ThenBy(p => p.Nombre);

    private static IQueryable<ProductoBasicoResponse> ProjectBasico(IQueryable<Producto> query) =>
        query.Select(p => new ProductoBasicoResponse(
            p.Id,
            p.Nombre,
            p.Precio,
            p.Activo,
            p.Badge,
            p.EsVegetariano,
            p.ImagenUrl,
            p.TiempoPreparacionMin,
            p.PermitirMediaMedia,
            p.AceptaSalsas,
            p.AceptaToppings,
            p.ToppingGruposCompatibles,
            p.CategoriaId,
            p.Categoria == null ? null : new CategoriaRefResponse(p.Categoria.Id, p.Categoria.Nombre),
            p.ProductoReceta.Any()));

    private static IQueryable<ProductoResponse> ProjectFull(IQueryable<Producto> query) =>
        query.Select(p => new ProductoResponse(
            p.Id,
            p.Nombre,
            p.Precio,
            p.Activo,
            p.EsParaVenta,
            p.Descripcion,
            p.Codigo,
            p.Badge,
            p.EsVegetariano,
            p.ImagenUrl,
            p.TiempoPreparacionMin,
            p.PermitirMediaMedia,
            p.AceptaSalsas,
            p.AceptaToppings,
            p.ToppingGruposCompatibles,
            p.CategoriaId,
            p.Categoria == null ? null : new CategoriaRefResponse(p.Categoria.Id, p.Categoria.Nombre),
            p.ProductoReceta
                .Select(r => new RecetaItemResponse(
                    r.InsumoId,
                    r.Cantidad,
                    new InsumoRefResponse(r.Insumo.Id, r.Insumo.Nombre, r.Insumo.UnidadMedida)))
                .ToList()));

    /// <summary>Valida que todos los insumoId de la receta existan en el negocio activo. Vacío ⇒ OK.</summary>
    private async Task<IActionResult?> ValidarInsumos(List<RecetaItemInput>? receta)
    {
        var ids = (receta ?? new List<RecetaItemInput>())
            .Select(r => r.InsumoId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var validos = await _db.Insumos.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync();
        return validos.Count == ids.Count
            ? null
            : BadRequest(new { message = "Algún insumo de la receta no existe o no pertenece al negocio." });
    }

    private static IEnumerable<RecetaItemInput> ItemsValidos(List<RecetaItemInput>? receta) =>
        (receta ?? new List<RecetaItemInput>())
            .Where(r => !string.IsNullOrWhiteSpace(r.InsumoId) && r.Cantidad > 0);

    private static List<string> DistinctIds(List<string>? ids) =>
        ids?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList() ?? new List<string>();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private ConflictObjectResult CodigoDuplicado(string codigo) =>
        Conflict(new { message = $"Ya existe un producto con el código '{codigo}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ForeignKeyViolation };
}
