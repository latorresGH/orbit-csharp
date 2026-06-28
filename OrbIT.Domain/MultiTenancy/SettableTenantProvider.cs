namespace OrbIT.Domain.MultiTenancy;

/// <summary>
/// Implementación de <see cref="ITenantProvider"/> cuyo tenant se fija manualmente.
/// Útil para tests de integración, jobs/background workers y cualquier contexto sin
/// <c>HttpContext</c>, donde el negocio activo se conoce de antemano o cambia entre
/// operaciones.
/// </summary>
public sealed class SettableTenantProvider : ITenantProvider
{
    public SettableTenantProvider()
    {
    }

    public SettableTenantProvider(string? negocioId)
    {
        NegocioId = negocioId;
    }

    /// <inheritdoc />
    public string? NegocioId { get; set; }
}
