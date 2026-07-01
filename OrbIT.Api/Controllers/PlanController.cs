using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Planes;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Gestión de los planes de suscripción del sistema. <c>Plan</c> es una tabla GLOBAL (no por tenant), por lo
/// que NO lleva Global Query Filter y se lee/escribe directo sin resolver negocio. Todas las acciones son
/// exclusivas de SUPERADMIN. La lógica es CRUD simple sobre una tabla chica: sin paginación ni proyecciones
/// especiales (no es cuello de botella). Pre-chequeo de slug duplicado → 409 antes de reventar la unique.
/// </summary>
[ApiController]
[Route("planes")]
[Authorize(Roles = "SUPERADMIN")]
public sealed class PlanController : ControllerBase
{
    private readonly OrbitDbContext _db;

    public PlanController(OrbitDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var planes = await _db.Plans.AsNoTracking()
            .OrderBy(p => p.PrecioMensual)
            .ThenBy(p => p.Nombre)
            .ToListAsync();
        return Ok(planes.Select(Map));
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> ObtenerPorSlug(string slug)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug);
        return plan is null ? NotFound(new { message = $"Plan \"{slug}\" no encontrado" }) : Ok(Map(plan));
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearPlanRequest request)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await _db.Plans.AnyAsync(p => p.Slug == slug))
        {
            return Conflict(new { message = $"Ya existe un plan con el slug \"{slug}\"" });
        }

        var plan = new Plan
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = request.Nombre.Trim(),
            Slug = slug,
            PrecioMensual = request.PrecioMensual,
            MpPlanId = string.IsNullOrWhiteSpace(request.MpPlanId) ? null : request.MpPlanId.Trim(),
            LimiteProductos = request.LimiteProductos,
            LimiteUsuarios = request.LimiteUsuarios,
            TieneMesas = request.TieneMesas,
            TieneImagenes = request.TieneImagenes,
            TieneSignalR = request.TieneSignalR,
            TieneReportes = request.TieneReportes,
            TieneToppingGrupos = request.TieneToppingGrupos,
            TieneOfertas = request.TieneOfertas,
            TieneInsumos = request.TieneInsumos,
            Activo = request.Activo,
            CreatedAt = Now(),
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(ObtenerPorSlug), new { slug = plan.Slug }, Map(plan));
    }

    [HttpPut("{slug}")]
    public async Task<IActionResult> Actualizar(string slug, [FromBody] ActualizarPlanRequest request)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Slug == slug);
        if (plan is null) return NotFound(new { message = $"Plan \"{slug}\" no encontrado" });

        if (!string.IsNullOrWhiteSpace(request.Nombre)) plan.Nombre = request.Nombre.Trim();
        if (request.PrecioMensual is { } precio) plan.PrecioMensual = precio;
        if (request.MpPlanId is not null) plan.MpPlanId = string.IsNullOrWhiteSpace(request.MpPlanId) ? null : request.MpPlanId.Trim();
        if (request.LimiteProductos is { } lp) plan.LimiteProductos = lp;
        if (request.LimiteUsuarios is { } lu) plan.LimiteUsuarios = lu;
        if (request.TieneMesas is { } m) plan.TieneMesas = m;
        if (request.TieneImagenes is { } i) plan.TieneImagenes = i;
        if (request.TieneSignalR is { } s) plan.TieneSignalR = s;
        if (request.TieneReportes is { } r) plan.TieneReportes = r;
        if (request.TieneToppingGrupos is { } tg) plan.TieneToppingGrupos = tg;
        if (request.TieneOfertas is { } o) plan.TieneOfertas = o;
        if (request.TieneInsumos is { } ins) plan.TieneInsumos = ins;
        if (request.Activo is { } activo) plan.Activo = activo;

        await _db.SaveChangesAsync();
        return Ok(Map(plan));
    }

    [HttpPatch("{slug}/activo")]
    public async Task<IActionResult> CambiarActivo(string slug, [FromBody] CambiarActivoPlanRequest request)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Slug == slug);
        if (plan is null) return NotFound(new { message = $"Plan \"{slug}\" no encontrado" });

        plan.Activo = request.Activo;
        await _db.SaveChangesAsync();
        return Ok(Map(plan));
    }

    private static PlanResponse Map(Plan p) => new(
        p.Id, p.Nombre, p.Slug, p.PrecioMensual, p.MpPlanId,
        p.LimiteProductos, p.LimiteUsuarios,
        p.TieneMesas, p.TieneImagenes, p.TieneSignalR, p.TieneReportes,
        p.TieneToppingGrupos, p.TieneOfertas, p.TieneInsumos,
        p.Activo, p.CreatedAt);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
