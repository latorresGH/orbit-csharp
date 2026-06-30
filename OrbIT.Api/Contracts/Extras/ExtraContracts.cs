using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Extras;

/// <summary>
/// Body para crear un extra/topping. La unicidad del nombre es por negocio (tenant); el
/// <c>NegocioId</c> NO viaja en el body: se estampa desde el tenant activo. Defaults de NestJS:
/// precio 500, stock 0, activo true, categoría normalizada 'TOPPINGS', unidad UNIDAD.
/// </summary>
/// <remarks>
/// Gotcha sistémico de sentinels EF (ver memoria de sentinels): <see cref="Precio"/> y
/// <see cref="Activo"/> tienen default en DB (500 y true), así que enviar el valor CLR default
/// (precio 0 / activo false) en el create hace que la base aplique SU default en vez del valor
/// enviado. Se trata a nivel modelo (HasSentinel/SaveChanges), no con un hack por-controller.
/// </remarks>
public sealed class CreateExtraRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double Precio { get; set; } = 500;

    [Range(0, double.MaxValue, ErrorMessage = "El stock no puede ser negativo.")]
    public double StockActual { get; set; }

    public bool Activo { get; set; } = true;

    /// <summary>Categoría libre del extra (se normaliza a MAYÚSCULAS; default 'TOPPINGS').</summary>
    public string? Categoria { get; set; }

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida UnidadMedida { get; set; } = UnidadMedida.UNIDAD;

    /// <summary>Insumo del que descuenta stock este extra (opcional). Se valida contra el tenant.</summary>
    public string? InsumoId { get; set; }

    public bool EsGlobal { get; set; }

    public bool EsPremium { get; set; }

    /// <summary>Grupo de toppings al que pertenece (opcional; un extra puede estar suelto). Se valida contra el tenant.</summary>
    public string? ToppingGrupoId { get; set; }

    /// <summary>Consumos de stock por categoría: <c>{ categoriaId: cantidad }</c>. Sólo se guardan los &gt; 0.</summary>
    public Dictionary<string, double>? Consumos { get; set; }

    /// <summary>Categorías de producto a las que aplica. Cada id se valida contra el tenant.</summary>
    public List<string>? CategoriaIds { get; set; }
}

/// <summary>
/// Body para actualizar un extra. Semántica PATCH parcial: sólo se modifican los campos presentes.
/// <see cref="CategoriaIds"/>/<see cref="Consumos"/> presentes (aunque vacíos) reemplazan por completo
/// el set correspondiente; ausentes lo dejan intacto. Para <see cref="InsumoId"/>/<see cref="ToppingGrupoId"/>:
/// <c>null</c> = no tocar; cadena vacía = desvincular (null); valor = vincular (validado contra el tenant).
/// </summary>
/// <remarks>
/// El <c>preciosPorCategoria</c> del DTO de NestJS se omite a propósito: el service de producción lo
/// ignora (campo muerto). Los precios por categoría se setean por <c>POST /extras/precio-categoria</c>.
/// </remarks>
public sealed class UpdateExtraRequest
{
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string? Nombre { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double? Precio { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El stock no puede ser negativo.")]
    public double? StockActual { get; set; }

    public bool? Activo { get; set; }

    public string? Categoria { get; set; }

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida? UnidadMedida { get; set; }

    public string? InsumoId { get; set; }

    public bool? EsGlobal { get; set; }

    public bool? EsPremium { get; set; }

    public string? ToppingGrupoId { get; set; }

    public Dictionary<string, double>? Consumos { get; set; }

    public List<string>? CategoriaIds { get; set; }
}

/// <summary>Body para asignar (upsert) el precio de un extra en una categoría de producto.</summary>
public sealed class SetExtraPrecioCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El extra es obligatorio.")]
    public string ExtraId { get; set; } = null!;

    [Required(AllowEmptyStrings = false, ErrorMessage = "La categoría es obligatoria.")]
    public string CategoriaId { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double Precio { get; set; }
}

/// <summary>Body para asignar (upsert) el consumo de stock de un extra por categoría. Debe ser &gt; 0.</summary>
public sealed class SetExtraConsumoCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El extra es obligatorio.")]
    public string ExtraId { get; set; } = null!;

    [Required(AllowEmptyStrings = false, ErrorMessage = "La categoría es obligatoria.")]
    public string CategoriaId { get; set; } = null!;

    [Range(0.0001, double.MaxValue, ErrorMessage = "El consumo debe ser mayor a cero.")]
    public double CantidadConsumo { get; set; }
}

/// <summary>Body para activar/desactivar un extra.</summary>
public sealed class ToggleExtraActivoRequest
{
    public bool Activo { get; set; }
}

/// <summary>Body para sumar o descontar stock. La cantidad debe ser &gt; 0; el motivo es opcional.</summary>
public sealed class ExtraStockMovRequest
{
    [Range(0.0001, double.MaxValue, ErrorMessage = "Cantidad inválida")]
    public double Cantidad { get; set; }

    public string? Motivo { get; set; }
}

/// <summary>Representación de salida de un extra con sus relaciones por categoría.</summary>
public sealed record ExtraResponse(
    string Id,
    string Nombre,
    UnidadMedida UnidadMedida,
    double Precio,
    double StockActual,
    bool Activo,
    string Categoria,
    bool EsGlobal,
    bool EsPremium,
    string? InsumoId,
    string? ToppingGrupoId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<ExtraCategoriaResponse> CategoriasAplica,
    IReadOnlyList<ExtraPrecioResponse> PreciosPorCategoria,
    IReadOnlyList<ExtraConsumoResponse> ConsumosPorCategoria);

/// <summary>Vínculo extra↔categoría de producto (a qué categorías aplica el extra).</summary>
public sealed record ExtraCategoriaResponse(string Id, string CategoriaId, string? CategoriaNombre);

/// <summary>Precio del extra para una categoría puntual.</summary>
public sealed record ExtraPrecioResponse(string Id, string CategoriaId, string? CategoriaNombre, double Precio);

/// <summary>Consumo de stock del extra por unidad de producto de una categoría.</summary>
public sealed record ExtraConsumoResponse(string Id, string CategoriaId, string? CategoriaNombre, double CantidadConsumo);
