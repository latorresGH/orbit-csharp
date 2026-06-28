namespace OrbIT.Domain.MultiTenancy;

/// <summary>
/// Provee el identificador del negocio (tenant) activo para la request/scope actual.
/// El <c>OrbitDbContext</c> lo consume en sus global query filters para aislar los
/// datos por negocio. La resolución concreta del tenant (claim JWT, header, etc.)
/// vive en la capa de aplicación/Api; el dominio sólo conoce esta abstracción.
/// </summary>
public interface ITenantProvider
{
    /// <summary>
    /// Id del negocio activo, o <c>null</c> si no hay tenant resuelto en el scope
    /// actual. Cuando es <c>null</c>, los filtros sólo dejan ver filas sin negocio.
    /// </summary>
    string? NegocioId { get; }
}
