using OrbIT.Domain.MultiTenancy;

namespace OrbIT.Api.MultiTenancy;

/// <summary>
/// Resuelve el negocio (tenant) activo a partir del <see cref="HttpContext"/> de la
/// request en curso. Orden de prioridad:
/// <list type="number">
///   <item>tenant resuelto fuera de banda (<see cref="TenantResolutionContext"/>), p. ej. por
///   <c>?negocio=slug</c> en endpoints públicos;</item>
///   <item>claim <c>negocioId</c> del usuario autenticado;</item>
///   <item>header <c>X-Negocio-Id</c> (fallback).</item>
/// </list>
/// Devuelve <c>null</c> si no hay request o no se puede resolver, en cuyo caso los query filters del
/// DbContext no exponen datos de ningún negocio (fail-closed).
/// </summary>
public sealed class HttpTenantProvider : ITenantProvider
{
    private const string NegocioIdClaim = "negocioId";
    private const string NegocioIdHeader = "X-Negocio-Id";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TenantResolutionContext _tenantResolution;

    public HttpTenantProvider(IHttpContextAccessor httpContextAccessor, TenantResolutionContext tenantResolution)
    {
        _httpContextAccessor = httpContextAccessor;
        _tenantResolution = tenantResolution;
    }

    /// <inheritdoc />
    public string? NegocioId
    {
        get
        {
            // 1) Tenant resuelto fuera de banda (slug de un endpoint público). Tiene prioridad
            //    porque, para una request anónima, es la única fuente de tenant disponible.
            if (!string.IsNullOrEmpty(_tenantResolution.NegocioId))
            {
                return _tenantResolution.NegocioId;
            }

            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return null;
            }

            var claim = httpContext.User?.FindFirst(NegocioIdClaim)?.Value;
            if (!string.IsNullOrEmpty(claim))
            {
                return claim;
            }

            if (httpContext.Request.Headers.TryGetValue(NegocioIdHeader, out var header))
            {
                var value = header.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
