using OrbIT.Domain.MultiTenancy;

namespace OrbIT.Api.MultiTenancy;

/// <summary>
/// Resuelve el negocio (tenant) activo a partir del <see cref="HttpContext"/> de la
/// request en curso. Prioriza el claim <c>negocioId</c> del usuario autenticado y,
/// como fallback (mientras no haya auth cableada), acepta el header
/// <c>X-Negocio-Id</c>. Devuelve <c>null</c> si no hay request o no se puede resolver,
/// en cuyo caso los query filters del DbContext no exponen datos de ningún negocio.
/// </summary>
public sealed class HttpTenantProvider : ITenantProvider
{
    private const string NegocioIdClaim = "negocioId";
    private const string NegocioIdHeader = "X-Negocio-Id";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? NegocioId
    {
        get
        {
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
