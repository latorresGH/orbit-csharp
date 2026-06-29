using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Categorias;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de categorías, scopeado por negocio (tenant) vía los global query filters del
/// <c>OrbitDbContext</c>. Lectura para cualquier usuario autenticado; escritura ADMIN-only.
/// </summary>
[ApiController]
[Route("categorias")]
[Authorize]
public sealed class CategoriasController : ControllerBase
{
    // Código SQLSTATE de violación de unicidad en PostgreSQL (backstop del pre-chequeo de nombre).
    private const string UniqueViolation = "23505";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public CategoriasController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // El query filter ya limita al negocio activo; sólo definimos el orden de salida.
        var categorias = await _db.Categoria
            .OrderBy(c => c.Orden)
            .ThenBy(c => c.Nombre)
            .ToListAsync();

        return Ok(categorias.Select(ToResponse));
    }

    [HttpGet("{id}", Name = nameof(GetCategoriaById))]
    public async Task<IActionResult> GetCategoriaById(string id)
    {
        // FirstOrDefaultAsync (no FindAsync) para que el query filter multi-tenant se aplique:
        // FindAsync puede resolver desde la caché del contexto y saltearse el filtro por negocio.
        var categoria = await _db.Categoria.FirstOrDefaultAsync(c => c.Id == id);
        return categoria is null ? NotFound() : Ok(ToResponse(categoria));
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateCategoriaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            // Un ADMIN siempre trae negocioId en el claim; si falta (p. ej. SUPERADMIN sin
            // negocio) no hay tenant donde crear la categoría.
            return Forbid();
        }

        // D3: pre-chequeo de nombre duplicado dentro del negocio (el query filter ya scopea).
        if (await _db.Categoria.AnyAsync(c => c.Nombre == request.Nombre))
        {
            return NombreDuplicado(request.Nombre);
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var categoria = new Categorium
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = request.Nombre,
            Descripcion = request.Descripcion,
            Activo = request.Activo,
            Orden = request.Orden,
            MaxAderezosGratis = request.MaxAderezosGratis,
            // ── Stamping temporal del tenant ─────────────────────────────────────
            // Hoy estampamos NegocioId a mano en cada Create. Es deliberadamente
            // explícito hasta que el OrbitDbContext lo haga de forma centralizada en
            // SaveChanges() (para toda entidad multi-tenant). Mientras tanto, olvidarlo
            // acá insertaría una fila con NegocioId inválido que ningún tenant podría ver.
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Categoria.Add(categoria);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Backstop: dos creates concurrentes pueden pasar ambos el pre-chequeo y chocar
            // contra el índice único (nombre, negocioId). Lo devolvemos como 409 igual.
            return NombreDuplicado(request.Nombre);
        }

        return CreatedAtAction(nameof(GetCategoriaById), new { id = categoria.Id }, ToResponse(categoria));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateCategoriaRequest request)
    {
        var categoria = await _db.Categoria.FirstOrDefaultAsync(c => c.Id == id);
        if (categoria is null)
        {
            return NotFound();
        }

        // D3 también en update: si cambia el nombre, no debe pisar el de otra categoría del negocio.
        if (!string.Equals(categoria.Nombre, request.Nombre, StringComparison.Ordinal)
            && await _db.Categoria.AnyAsync(c => c.Nombre == request.Nombre && c.Id != id))
        {
            return NombreDuplicado(request.Nombre);
        }

        categoria.Nombre = request.Nombre;
        categoria.Descripcion = request.Descripcion;
        categoria.Activo = request.Activo;
        categoria.Orden = request.Orden;
        categoria.MaxAderezosGratis = request.MaxAderezosGratis;
        categoria.UpdatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NombreDuplicado(request.Nombre);
        }

        return Ok(ToResponse(categoria));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var categoria = await _db.Categoria.FirstOrDefaultAsync(c => c.Id == id);
        if (categoria is null)
        {
            return NotFound();
        }

        _db.Categoria.Remove(categoria);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // La FK de Producto -> Categoria es Restrict: no se puede borrar una categoría con
            // productos asociados. Lo reportamos como conflicto en vez de un 500.
            return Conflict(new { message = "No se puede eliminar la categoría: tiene productos asociados." });
        }

        return NoContent();
    }

    /// <summary>
    /// D4: reordena las categorías. El cuerpo es <c>{ "idsEnOrden": ["id1", "id2", ...] }</c>
    /// y el índice de cada id define su nuevo <c>Orden</c> (0-based).
    /// </summary>
    [HttpPatch("orden")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Reorder([FromBody] ReorderCategoriasRequest request)
    {
        var ids = request.IdsEnOrden;
        if (ids.Distinct().Count() != ids.Count)
        {
            return BadRequest(new { message = "La lista de ids no puede contener duplicados." });
        }

        // El Where + query filter garantiza que sólo traemos categorías del negocio activo;
        // si algún id no existe o es de otro negocio, el conteo no coincide y rechazamos todo.
        var categorias = await _db.Categoria.Where(c => ids.Contains(c.Id)).ToListAsync();
        if (categorias.Count != ids.Count)
        {
            return BadRequest(new { message = "Algún id no existe o no pertenece al negocio." });
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var porId = categorias.ToDictionary(c => c.Id);
        for (var i = 0; i < ids.Count; i++)
        {
            var categoria = porId[ids[i]];
            categoria.Orden = i;
            categoria.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();

        return Ok(categorias.OrderBy(c => c.Orden).Select(ToResponse));
    }

    private ConflictObjectResult NombreDuplicado(string nombre) =>
        Conflict(new { message = $"Ya existe una categoría con el nombre '{nombre}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static CategoriaResponse ToResponse(Categorium c) => new(
        c.Id,
        c.Nombre,
        c.Descripcion,
        c.Activo,
        c.Orden,
        c.MaxAderezosGratis,
        c.CreatedAt,
        c.UpdatedAt);
}
