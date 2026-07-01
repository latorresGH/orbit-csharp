using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Planes;

// ── Requests SUPERADMIN ──────────────────────────────────────────────────────

public sealed class CrearPlanRequest
{
    [Required, MinLength(2)]
    public string Nombre { get; set; } = null!;

    [Required, MinLength(2), MaxLength(30)]
    [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "El slug solo puede tener letras minúsculas, números y guiones")]
    public string Slug { get; set; } = null!;

    public double PrecioMensual { get; set; }

    public string? MpPlanId { get; set; }

    // -1 = ilimitado. Defaults alineados con el plan basic.
    public int LimiteProductos { get; set; } = 30;
    public int LimiteUsuarios { get; set; } = 3;

    public bool TieneMesas { get; set; }
    public bool TieneImagenes { get; set; }
    public bool TieneSignalR { get; set; }
    public bool TieneReportes { get; set; }
    public bool TieneToppingGrupos { get; set; }
    public bool TieneOfertas { get; set; }
    public bool TieneInsumos { get; set; }

    public bool Activo { get; set; } = true;
}

/// <summary>PUT: reemplaza los campos editables del plan (el slug es la clave y no se cambia acá).</summary>
public sealed class ActualizarPlanRequest
{
    public string? Nombre { get; set; }
    public double? PrecioMensual { get; set; }
    public string? MpPlanId { get; set; }
    public int? LimiteProductos { get; set; }
    public int? LimiteUsuarios { get; set; }
    public bool? TieneMesas { get; set; }
    public bool? TieneImagenes { get; set; }
    public bool? TieneSignalR { get; set; }
    public bool? TieneReportes { get; set; }
    public bool? TieneToppingGrupos { get; set; }
    public bool? TieneOfertas { get; set; }
    public bool? TieneInsumos { get; set; }
    public bool? Activo { get; set; }
}

public sealed class CambiarActivoPlanRequest
{
    public bool Activo { get; set; }
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record PlanResponse(
    string Id,
    string Nombre,
    string Slug,
    double PrecioMensual,
    string? MpPlanId,
    int LimiteProductos,
    int LimiteUsuarios,
    bool TieneMesas,
    bool TieneImagenes,
    bool TieneSignalR,
    bool TieneReportes,
    bool TieneToppingGrupos,
    bool TieneOfertas,
    bool TieneInsumos,
    bool Activo,
    DateTime CreatedAt);
