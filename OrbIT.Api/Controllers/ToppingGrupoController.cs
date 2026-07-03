using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.ToppingGrupos;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de grupos de toppings, scopeado por negocio (tenant) vía los global query filters del
/// <c>OrbitDbContext</c>. Lectura para cualquier usuario autenticado; escritura ADMIN-only.
///
/// Paridad con el backend NestJS (topping-grupos): NO hay unicidad de nombre (se permiten
/// repetidos) y el DELETE no se bloquea por Extras asociados — el FK <c>Extra.toppingGrupoId</c>
/// es ON DELETE SET NULL, así que borrar un grupo deja sus Extras huérfanos (toppingGrupoId = null),
/// que es el comportamiento de diseño (un Extra puede existir sin grupo).
/// </summary>
[ApiController]
[Route("topping-grupos")]
[Authorize]
public sealed class ToppingGrupoController : ControllerBase
{
    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public ToppingGrupoController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // El query filter ya limita al negocio activo; sólo definimos el orden de salida. Projection directa
        // al DTO en SQL (AsNoTracking + Select): no materializamos la entidad ni tocamos el change tracker.
        var grupos = await _db.ToppingGrupos.AsNoTracking()
            .OrderBy(g => g.Orden)
            .ThenBy(g => g.Nombre)
            .Select(Projection)
            .ToListAsync();

        return Ok(grupos);
    }

    [HttpGet("{id}", Name = nameof(GetToppingGrupoById))]
    public async Task<IActionResult> GetToppingGrupoById(string id)
    {
        // Where + query filter multi-tenant (no FindAsync, que puede resolver de caché y saltearse el filtro).
        // Projection directa al DTO en SQL.
        var grupo = await _db.ToppingGrupos.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(Projection)
            .FirstOrDefaultAsync();
        return grupo is null ? NotFound() : Ok(grupo);
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateToppingGrupoRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            // Un ADMIN siempre trae negocioId en el claim; si falta no hay tenant donde crear.
            return Forbid();
        }

        var grupo = new ToppingGrupo
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = request.Nombre.Trim(),
            MaxExtrasGratis = request.MaxExtrasGratis,
            EsIncluido = request.EsIncluido,
            Orden = request.Orden,
            Activo = true,
            // ── Stamping temporal del tenant ─────────────────────────────────────
            // Igual que en CategoriasController: estampamos NegocioId a mano hasta que el
            // OrbitDbContext lo haga centralizado en SaveChanges() para toda entidad
            // multi-tenant. Olvidarlo acá insertaría una fila que ningún tenant podría ver.
            NegocioId = negocioId,
        };

        // Gotcha conocido y SISTÉMICO del scaffold (no específico de este controller): si el
        // cliente manda MaxExtrasGratis=0 — el valor CLR default de un int con default en DB —
        // EF lo omite del INSERT y la base aplica su default (3). Se trata aparte a nivel modelo
        // (HasSentinel / nullabilidad); ver memoria de sentinels EF. No lo forzamos acá.
        _db.ToppingGrupos.Add(grupo);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetToppingGrupoById), new { id = grupo.Id }, ToResponse(grupo));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateToppingGrupoRequest request)
    {
        var grupo = await _db.ToppingGrupos.FirstOrDefaultAsync(g => g.Id == id);
        if (grupo is null)
        {
            return NotFound();
        }

        grupo.Nombre = request.Nombre.Trim();
        grupo.MaxExtrasGratis = request.MaxExtrasGratis;
        grupo.EsIncluido = request.EsIncluido;
        grupo.Orden = request.Orden;
        grupo.Activo = request.Activo;

        // En UPDATE no aplica el gotcha del sentinel: EF compara original vs actual sobre una
        // fila existente, así que false/0 se persisten sin necesidad de forzar nada.
        await _db.SaveChangesAsync();

        return Ok(ToResponse(grupo));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        var grupo = await _db.ToppingGrupos.FirstOrDefaultAsync(g => g.Id == id);
        if (grupo is null)
        {
            return NotFound();
        }

        // No bloqueamos por Extras asociados: el FK Extra.toppingGrupoId es ON DELETE SET NULL,
        // así que la base deja los Extras huérfanos (toppingGrupoId = null). Paridad con NestJS.
        _db.ToppingGrupos.Remove(grupo);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static ToppingGrupoResponse ToResponse(ToppingGrupo g) => new(
        g.Id,
        g.Nombre,
        g.MaxExtrasGratis,
        g.EsIncluido,
        g.Orden,
        g.Activo);

    // Misma forma que ToResponse pero como Expression, para que EF la traduzca a SQL en los reads (projection).
    private static readonly System.Linq.Expressions.Expression<Func<ToppingGrupo, ToppingGrupoResponse>> Projection = g => new ToppingGrupoResponse(
        g.Id,
        g.Nombre,
        g.MaxExtrasGratis,
        g.EsIncluido,
        g.Orden,
        g.Activo);
}
