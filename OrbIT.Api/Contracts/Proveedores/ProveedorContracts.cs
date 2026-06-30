using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Proveedores;

/// <summary>
/// Body para crear un proveedor. El <c>NegocioId</c> NO viaja en el body: se estampa en el servidor
/// desde el tenant activo. La unicidad del nombre es por negocio (índice <c>Proveedor_nombre_negocioId_key</c>).
/// </summary>
public sealed class CreateProveedorRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    public string? Telefono { get; set; }

    [EmailAddress(ErrorMessage = "El email no es válido.")]
    public string? Email { get; set; }

    public string? Notas { get; set; }
}

/// <summary>
/// Body para actualizar un proveedor. Semántica PATCH parcial (paridad con NestJS): sólo se modifican
/// los campos presentes; los <c>null</c> se dejan como están.
/// </summary>
public sealed class UpdateProveedorRequest
{
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string? Nombre { get; set; }

    public string? Telefono { get; set; }

    [EmailAddress(ErrorMessage = "El email no es válido.")]
    public string? Email { get; set; }

    public string? Notas { get; set; }

    public bool? Activo { get; set; }
}

/// <summary>Body para activar/desactivar un proveedor (baja/alta lógica).</summary>
public sealed class SetProveedorActivoRequest
{
    public bool Activo { get; set; }
}

/// <summary>Representación de salida de un proveedor (listado / create / update).</summary>
public sealed record ProveedorResponse(
    string Id,
    string Nombre,
    string? Telefono,
    string? Email,
    string? Notas,
    bool Activo,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Detalle de un proveedor con sus insumos asignados (GET /proveedores/{id}).</summary>
public sealed record ProveedorDetalleResponse(
    string Id,
    string Nombre,
    string? Telefono,
    string? Email,
    string? Notas,
    bool Activo,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<ProveedorInsumoResponse> Insumos);

/// <summary>Insumo asignado a un proveedor (proyección liviana para el detalle).</summary>
public sealed record ProveedorInsumoResponse(
    string Id,
    string Nombre,
    double StockActual,
    bool Activo,
    UnidadMedida UnidadMedida);
