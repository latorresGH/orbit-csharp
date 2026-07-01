using Microsoft.EntityFrameworkCore;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Planes;

/// <summary>
/// Implementación de <see cref="IPlanGuard"/>. Resuelve el plan del negocio y compara contra los conteos
/// reales. Los conteos usan <c>IgnoreQueryFilters</c> + filtro explícito por <c>negocioId</c> para ser
/// deterministas sin depender de que el tenant activo del request coincida con el negocio consultado.
/// <c>Plan</c> es tabla global (sin Global Query Filter), así que se lee directo.
/// </summary>
public sealed class PlanGuard : IPlanGuard
{
    private const string PlanBasicoSlug = "basic";

    private readonly OrbitDbContext _db;

    public PlanGuard(OrbitDbContext db) => _db = db;

    public async Task<LimiteResult> VerificarLimiteProductosAsync(string negocioId, CancellationToken ct = default)
    {
        var plan = await ResolverPlanAsync(negocioId, ct);
        var limite = plan?.LimiteProductos ?? 0;

        var actual = await _db.Productos.IgnoreQueryFilters()
            .CountAsync(p => p.NegocioId == negocioId && p.Activo, ct);

        return new LimiteResult(HayLugar(actual, limite), actual, limite);
    }

    public async Task<LimiteResult> VerificarLimiteUsuariosAsync(string negocioId, CancellationToken ct = default)
    {
        var plan = await ResolverPlanAsync(negocioId, ct);
        var limite = plan?.LimiteUsuarios ?? 0;

        var actual = await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.NegocioId == negocioId && u.Activo, ct);

        return new LimiteResult(HayLugar(actual, limite), actual, limite);
    }

    public async Task<bool> VerificarFeatureAsync(string negocioId, PlanFeature feature, CancellationToken ct = default)
    {
        var plan = await ResolverPlanAsync(negocioId, ct);
        if (plan is null) return false;

        return feature switch
        {
            PlanFeature.Mesas => plan.TieneMesas,
            PlanFeature.Imagenes => plan.TieneImagenes,
            PlanFeature.SignalR => plan.TieneSignalR,
            PlanFeature.Reportes => plan.TieneReportes,
            PlanFeature.ToppingGrupos => plan.TieneToppingGrupos,
            PlanFeature.Ofertas => plan.TieneOfertas,
            PlanFeature.Insumos => plan.TieneInsumos,
            _ => false,
        };
    }

    /// <summary>
    /// Plan del negocio: el asignado por <c>planId</c>, o el plan <c>basic</c> como fallback (fail-closed hacia
    /// el más restrictivo). Devuelve null sólo si el negocio no existe y no hay plan basic seeded.
    /// </summary>
    private async Task<Plan?> ResolverPlanAsync(string negocioId, CancellationToken ct)
    {
        var plan = await _db.Negocios.IgnoreQueryFilters()
            .Where(n => n.Id == negocioId)
            .Select(n => n.PlanNavigation)
            .FirstOrDefaultAsync(ct);

        return plan ?? await _db.Plans.FirstOrDefaultAsync(p => p.Slug == PlanBasicoSlug, ct);
    }

    /// <summary>Hay lugar para uno más: límite negativo = ilimitado; si no, actual &lt; límite.</summary>
    private static bool HayLugar(int actual, int limite) => limite < 0 || actual < limite;
}
