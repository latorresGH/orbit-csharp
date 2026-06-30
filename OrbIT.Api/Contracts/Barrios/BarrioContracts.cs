using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Barrios;

/// <summary>
/// Body para crear un barrio (zona de envío). El <c>NegocioId</c> NO viaja en el body: se estampa en el
/// servidor desde el tenant activo. La unicidad del nombre es por negocio (<c>Barrio_nombre_negocioId_key</c>).
/// </summary>
public sealed class CreateBarrioRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El precio de envío no puede ser negativo.")]
    public double PrecioEnvio { get; set; }

    /// <summary>Activo. Si se omite, default <c>true</c> (paridad con NestJS: <c>activo ?? true</c>).</summary>
    public bool? Activo { get; set; }
}

/// <summary>
/// Body para actualizar un barrio. Semántica PATCH parcial (paridad con NestJS): sólo se modifican los
/// campos presentes; los <c>null</c> se dejan como están.
/// </summary>
public sealed class UpdateBarrioRequest
{
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string? Nombre { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El precio de envío no puede ser negativo.")]
    public double? PrecioEnvio { get; set; }

    public bool? Activo { get; set; }
}

/// <summary>Representación de salida de un barrio.</summary>
public sealed record BarrioResponse(
    string Id,
    string Nombre,
    double PrecioEnvio,
    bool Activo,
    DateTime CreatedAt,
    DateTime UpdatedAt);
