using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.MultiTenancy;

/// <summary>
/// Resource filter que replica el <c>OptionalJwtGuard</c> de NestJS: deja resuelto el negocio (tenant)
/// activo para endpoints accesibles tanto por usuarios autenticados como por clientes anónimos
/// (storefront público). Se engancha vía <see cref="AllowAnonymousWithTenantAttribute"/>.
///
/// Corre como resource filter, es decir DESPUÉS de <c>UseAuthentication</c> (el <c>User</c>/claim ya
/// está poblado aunque el endpoint sea anónimo — autenticación ≠ autorización) y ANTES del model
/// binding. Lógica:
/// <list type="number">
///   <item>Hay claim <c>negocioId</c> (usuario logueado) → no hace nada: el <see cref="HttpTenantProvider"/>
///   ya lo toma del claim.</item>
///   <item>No hay claim pero viene <c>?negocio=slug</c> → resuelve el negocio por slug
///   (con <c>IgnoreQueryFilters</c>, porque acá estamos buscando el negocio EN SÍ, todavía sin tenant)
///   y lo deja en el <see cref="TenantResolutionContext"/>. Slug inexistente → 404.</item>
///   <item>Ni claim ni slug → 400.</item>
/// </list>
/// El único <c>IgnoreQueryFilters</c> del flujo público vive acá; los controllers siguen confiando en
/// el Global Query Filter sin tocarlo.
/// </summary>
public sealed class ResolveTenantBySlugFilter : IAsyncResourceFilter
{
    private const string NegocioIdClaim = "negocioId";
    private const string SlugQueryParam = "negocio";

    private readonly OrbitDbContext _db;
    private readonly TenantResolutionContext _tenantResolution;

    public ResolveTenantBySlugFilter(OrbitDbContext db, TenantResolutionContext tenantResolution)
    {
        _db = db;
        _tenantResolution = tenantResolution;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;

        // 1) Usuario autenticado: el claim alcanza, el HttpTenantProvider lo resuelve solo.
        var claim = http.User?.FindFirst(NegocioIdClaim)?.Value;
        if (!string.IsNullOrEmpty(claim))
        {
            await next();
            return;
        }

        // 2) Anónimo: el tenant se resuelve desde ?negocio=slug.
        var slug = http.Request.Query[SlugQueryParam].ToString();
        if (string.IsNullOrEmpty(slug))
        {
            context.Result = new BadRequestObjectResult(new { message = "Se requiere el parámetro 'negocio'." });
            return;
        }

        var negocioId = await _db.Negocios
            .IgnoreQueryFilters()
            .Where(n => n.Slug == slug)
            .Select(n => n.Id)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (negocioId is null)
        {
            context.Result = new NotFoundObjectResult(new { message = $"Negocio '{slug}' no encontrado." });
            return;
        }

        _tenantResolution.Resolve(negocioId);
        await next();
    }
}
