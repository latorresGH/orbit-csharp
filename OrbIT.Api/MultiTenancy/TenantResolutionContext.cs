namespace OrbIT.Api.MultiTenancy;

/// <summary>
/// Portador scoped (una instancia por request) del negocio (tenant) resuelto <b>fuera de banda</b>,
/// es decir, no desde el JWT sino por otro medio — hoy, el slug de <c>?negocio=</c> en los endpoints
/// públicos. Lo escribe <see cref="ResolveTenantBySlugFilter"/> y lo lee <see cref="HttpTenantProvider"/>
/// con prioridad sobre el claim.
///
/// La gracia de tenerlo separado: los Global Query Filters del <c>OrbitDbContext</c> leen
/// <c>ITenantProvider.NegocioId</c> de forma diferida en cada query, así que con sólo dejar acá el id
/// resuelto el filtro multi-tenant sigue funcionando de forma transparente en los endpoints públicos,
/// sin un solo <c>IgnoreQueryFilters</c> repartido por los controllers.
/// </summary>
public sealed class TenantResolutionContext
{
    /// <summary>Negocio resuelto fuera de banda, o <c>null</c> si la request no pasó por ese camino.</summary>
    public string? NegocioId { get; private set; }

    /// <summary>Fija el negocio activo para el resto de la request. Idempotente por diseño.</summary>
    public void Resolve(string negocioId) => NegocioId = negocioId;
}
