namespace OrbIT.Api.Contracts.Billing;

/// <summary>Respuesta del checkout: la preferencia de MP creada y su punto de inicio para redirigir al pago.</summary>
public sealed record CheckoutResponse(string PreferenceId, string InitPoint, string? SandboxInitPoint);

/// <summary>Suscripción actual del negocio: el plan contratado (si hay) + el estado del plan.</summary>
public sealed record MiSuscripcionResponse(
    string NegocioId,
    string EstadoPlan,
    PlanSuscripcionResponse? Plan,
    string? MpSuscripcionId);

public sealed record PlanSuscripcionResponse(
    string Id,
    string Nombre,
    string Slug,
    double PrecioMensual,
    int LimiteProductos,
    int LimiteUsuarios,
    bool TieneMesas,
    bool TieneImagenes,
    bool TieneSignalR,
    bool TieneReportes,
    bool TieneToppingGrupos,
    bool TieneOfertas,
    bool TieneInsumos);

public sealed record CancelarSuscripcionResponse(bool Ok, string PlanSlug, bool CanceladoEnMp);
