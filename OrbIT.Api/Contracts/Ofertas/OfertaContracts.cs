using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Ofertas;

// ── Requests anidados ────────────────────────────────────────────────────────

/// <summary>Producto asociado a una oferta (2x1, %, monto fijo) o precio especial de combo.</summary>
public sealed class OfertaProductoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El productoId es obligatorio.")]
    public string ProductoId { get; set; } = null!;

    public bool? Obligatorio { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "cantidadMin debe ser mayor o igual a 1.")]
    public int? CantidadMin { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "cantidadMax debe ser mayor o igual a 1.")]
    public int? CantidadMax { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "precioEspecial no puede ser negativo.")]
    public double? PrecioEspecial { get; set; }
}

/// <summary>Opción (producto) dentro de un grupo de combo.</summary>
public sealed class GrupoOpcionRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El productoId es obligatorio.")]
    public string ProductoId { get; set; } = null!;
}

/// <summary>Grupo de un combo: N unidades elegibles entre sus opciones.</summary>
public sealed class GrupoComboRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre del grupo es obligatorio.")]
    public string Nombre { get; set; } = null!;

    public bool? Obligatorio { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "La cantidad debe ser mayor o igual a 1.")]
    public int Cantidad { get; set; }

    [Required, MinLength(1, ErrorMessage = "El grupo debe tener al menos una opción.")]
    public List<GrupoOpcionRequest> Opciones { get; set; } = new();
}

// ── Create / Update / Activa ─────────────────────────────────────────────────

/// <summary>
/// Body para crear una oferta. <c>FechaInicio</c>/<c>FechaFin</c> son fechas <c>"yyyy-MM-dd"</c> (se
/// guardan como medianoche local). El <c>NegocioId</c> se estampa en el servidor.
/// </summary>
public sealed class CreateOfertaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1)]
    public string Nombre { get; set; } = null!;

    public string? Descripcion { get; set; }

    [Required(ErrorMessage = "El tipo es obligatorio.")]
    public TipoOferta Tipo { get; set; }

    public EstadoOferta? Estado { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "La fecha de inicio es obligatoria.")]
    public string FechaInicio { get; set; } = null!;

    public string? FechaFin { get; set; }

    public bool? Activa { get; set; }

    [Range(0, 100, ErrorMessage = "El porcentaje debe estar entre 0 y 100.")]
    public double? PorcentajeDescuento { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El monto no puede ser negativo.")]
    public double? MontoDescuento { get; set; }

    public int? MaxUsosPorCliente { get; set; }

    public int? MaxUsosTotales { get; set; }

    /// <summary>CSV de días aplicables (1=lunes .. 7=domingo). Default "1,2,3,4,5,6,7".</summary>
    public string? DiasAplicables { get; set; }

    public string? HoraInicio { get; set; }

    public string? HoraFin { get; set; }

    public bool? AplicaPorLinea { get; set; }

    public List<OfertaProductoRequest>? Productos { get; set; }

    public List<GrupoComboRequest>? GruposCombo { get; set; }
}

/// <summary>
/// Body para actualizar una oferta (PATCH parcial). Si <see cref="Productos"/> / <see cref="GruposCombo"/>
/// vienen (aun vacíos) se reemplazan por completo; si son <c>null</c> se dejan intactos.
/// </summary>
public sealed class UpdateOfertaRequest
{
    [StringLength(120, MinimumLength = 1)]
    public string? Nombre { get; set; }

    public string? Descripcion { get; set; }

    public TipoOferta? Tipo { get; set; }

    public EstadoOferta? Estado { get; set; }

    public string? FechaInicio { get; set; }

    public string? FechaFin { get; set; }

    public bool? Activa { get; set; }

    [Range(0, 100, ErrorMessage = "El porcentaje debe estar entre 0 y 100.")]
    public double? PorcentajeDescuento { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El monto no puede ser negativo.")]
    public double? MontoDescuento { get; set; }

    public int? MaxUsosPorCliente { get; set; }

    public int? MaxUsosTotales { get; set; }

    public string? DiasAplicables { get; set; }

    public string? HoraInicio { get; set; }

    public string? HoraFin { get; set; }

    public bool? AplicaPorLinea { get; set; }

    public List<OfertaProductoRequest>? Productos { get; set; }

    public List<GrupoComboRequest>? GruposCombo { get; set; }
}

/// <summary>Body para activar/desactivar una oferta.</summary>
public sealed class SetOfertaActivaRequest
{
    public bool Activa { get; set; }
}

// ── Preview (cálculo) ────────────────────────────────────────────────────────

/// <summary>Extra de una línea para el preview (no entra en el cálculo de ofertas, se acepta por paridad).</summary>
public sealed class PreviewExtraRequest
{
    [Required(AllowEmptyStrings = false)]
    public string ExtraId { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int Cantidad { get; set; }

    public double? Precio { get; set; }
}

/// <summary>Línea de carrito para previsualizar el descuento.</summary>
public sealed class PreviewLineaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El productoId es obligatorio.")]
    public string ProductoId { get; set; } = null!;

    [Range(1, int.MaxValue, ErrorMessage = "La cantidad debe ser mayor o igual a 1.")]
    public int Cantidad { get; set; }

    public double? PrecioUnitario { get; set; }

    public List<PreviewExtraRequest>? Extras { get; set; }

    public bool? MediaMedia { get; set; }
}

/// <summary>Body de <c>POST /ofertas/calcular</c>.</summary>
public sealed class PreviewOfertaRequest
{
    [Required, MinLength(1, ErrorMessage = "Se requiere al menos una línea.")]
    public List<PreviewLineaRequest> Lineas { get; set; } = new();
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record OfertaProductoResponse(
    string Id, string ProductoId, string? NombreProducto, bool Obligatorio,
    int CantidadMin, int? CantidadMax, double? PrecioEspecial);

public sealed record GrupoOpcionResponse(string Id, string ProductoId, string? NombreProducto);

public sealed record GrupoComboResponse(
    string Id, string Nombre, bool Obligatorio, int Cantidad, IReadOnlyList<GrupoOpcionResponse> Opciones);

public sealed record OfertaResponse(
    string Id,
    string Nombre,
    string? Descripcion,
    TipoOferta Tipo,
    EstadoOferta Estado,
    DateTime FechaInicio,
    DateTime? FechaFin,
    bool Activa,
    double? PorcentajeDescuento,
    double? MontoDescuento,
    int? MaxUsosPorCliente,
    int? MaxUsosTotales,
    int UsosActuales,
    string DiasAplicables,
    string? HoraInicio,
    string? HoraFin,
    bool AplicaPorLinea,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<OfertaProductoResponse> Productos,
    IReadOnlyList<GrupoComboResponse> GruposCombo);
