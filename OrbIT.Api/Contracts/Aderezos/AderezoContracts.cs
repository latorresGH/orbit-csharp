using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Aderezos;

/// <summary>
/// Body para crear un aderezo/salsa. La unicidad del nombre es por negocio (tenant); el
/// <c>NegocioId</c> NO viaja en el body: se estampa en el servidor desde el tenant activo.
/// Defaults idénticos al NestJS de producción: stock 999, unidad UNIDAD, precio 0, premium/global false.
/// </summary>
public sealed class CreateAderezoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    /// <summary>Stock inicial. Default 999 (paridad con NestJS: un aderezo "siempre disponible").</summary>
    [Range(0, double.MaxValue, ErrorMessage = "El stock no puede ser negativo.")]
    public double StockActual { get; set; } = 999;

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida UnidadMedida { get; set; } = UnidadMedida.UNIDAD;

    public bool EsGlobal { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double Precio { get; set; }

    public bool EsPremium { get; set; }

    /// <summary>
    /// Categorías de producto a las que aplica este aderezo. Opcional. Cada id se valida contra el
    /// negocio activo (no se aceptan categorías de otro tenant).
    /// </summary>
    public List<string>? CategoriaIds { get; set; }
}

/// <summary>
/// Body para actualizar un aderezo. Semántica PATCH parcial (paridad con NestJS): sólo se modifican
/// los campos presentes; los <c>null</c> se dejan como están. <see cref="CategoriaIds"/> presente
/// (aunque vacío) reemplaza por completo el set de categorías; ausente lo deja intacto.
/// </summary>
public sealed class UpdateAderezoRequest
{
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string? Nombre { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El stock no puede ser negativo.")]
    public double? StockActual { get; set; }

    public bool? Activo { get; set; }

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida? UnidadMedida { get; set; }

    public bool? EsGlobal { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double? Precio { get; set; }

    public bool? EsPremium { get; set; }

    public List<string>? CategoriaIds { get; set; }
}

/// <summary>
/// Body para asignar (upsert) el precio de un aderezo en una categoría de producto. El query filter
/// garantiza estructuralmente que tanto el aderezo como la categoría sean del tenant activo.
/// </summary>
public sealed class SetPrecioCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El aderezo es obligatorio.")]
    public string AderezoId { get; set; } = null!;

    [Required(AllowEmptyStrings = false, ErrorMessage = "La categoría es obligatoria.")]
    public string CategoriaId { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double Precio { get; set; }
}

/// <summary>
/// Body para asignar (upsert) el consumo de stock de un aderezo por unidad de producto de una
/// categoría. <see cref="CantidadConsumo"/> debe ser &gt; 0 (paridad con NestJS: <c>Min(0.0001)</c>).
/// </summary>
public sealed class SetConsumoCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El aderezo es obligatorio.")]
    public string AderezoId { get; set; } = null!;

    [Required(AllowEmptyStrings = false, ErrorMessage = "La categoría es obligatoria.")]
    public string CategoriaId { get; set; } = null!;

    [Range(0.0001, double.MaxValue, ErrorMessage = "El consumo debe ser mayor a cero.")]
    public double CantidadConsumo { get; set; }
}

/// <summary>Body para activar/desactivar un aderezo.</summary>
public sealed class SetActivoRequest
{
    public bool Activo { get; set; }
}

/// <summary>Body para sumar o descontar stock. La cantidad debe ser &gt; 0.</summary>
public sealed class AjusteStockRequest
{
    [Range(0.0001, double.MaxValue, ErrorMessage = "Cantidad inválida")]
    public double Cantidad { get; set; }
}

/// <summary>Representación de salida de un aderezo con sus relaciones por categoría.</summary>
public sealed record AderezoResponse(
    string Id,
    string Nombre,
    UnidadMedida UnidadMedida,
    bool Activo,
    double StockActual,
    bool EsGlobal,
    bool EsPremium,
    double Precio,
    IReadOnlyList<AderezoCategoriaResponse> CategoriasAplica,
    IReadOnlyList<AderezoPrecioResponse> PreciosPorCategoria,
    IReadOnlyList<AderezoConsumoResponse> ConsumosPorCategoria);

/// <summary>Vínculo aderezo↔categoría de producto (a qué categorías aplica el aderezo).</summary>
public sealed record AderezoCategoriaResponse(string Id, string CategoriaId, string? CategoriaNombre);

/// <summary>Precio del aderezo para una categoría puntual.</summary>
public sealed record AderezoPrecioResponse(string Id, string CategoriaId, string? CategoriaNombre, double Precio);

/// <summary>Consumo de stock del aderezo por unidad de producto de una categoría.</summary>
public sealed record AderezoConsumoResponse(string Id, string CategoriaId, string? CategoriaNombre, double CantidadConsumo);
