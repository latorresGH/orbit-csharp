using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.CodigosDescuento;

/// <summary>
/// Body para crear un código de descuento. El código se normaliza UPPER+trim; la unicidad es por negocio
/// (índice <c>CodigoDescuento_codigo_negocioId_key</c>). <c>TipoDescuento</c> es un string acotado
/// (no enum, igual que el scaffold/NestJS): solo <c>PORCENTAJE</c> o <c>MONTO_FIJO</c>.
/// </summary>
public sealed class CreateCodigoDescuentoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El código es obligatorio.")]
    [StringLength(40, MinimumLength = 1)]
    public string Codigo { get; set; } = null!;

    public string? Descripcion { get; set; }

    [Required(ErrorMessage = "El tipo de descuento es obligatorio.")]
    [AllowedValues("PORCENTAJE", "MONTO_FIJO", ErrorMessage = "tipoDescuento debe ser PORCENTAJE o MONTO_FIJO.")]
    public string TipoDescuento { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El valor no puede ser negativo.")]
    public double Valor { get; set; }

    public string? ProductoId { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "La fecha de inicio es obligatoria.")]
    public string FechaInicio { get; set; } = null!;

    [Required(AllowEmptyStrings = false, ErrorMessage = "La fecha de fin es obligatoria.")]
    public string FechaFin { get; set; } = null!;

    public bool? Activo { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "usosMaximos debe ser mayor o igual a 1.")]
    public int? UsosMaximos { get; set; }
}

/// <summary>Body para actualizar un código (PATCH parcial).</summary>
public sealed class UpdateCodigoDescuentoRequest
{
    [StringLength(40, MinimumLength = 1)]
    public string? Codigo { get; set; }

    public string? Descripcion { get; set; }

    [AllowedValues("PORCENTAJE", "MONTO_FIJO", ErrorMessage = "tipoDescuento debe ser PORCENTAJE o MONTO_FIJO.")]
    public string? TipoDescuento { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El valor no puede ser negativo.")]
    public double? Valor { get; set; }

    public string? ProductoId { get; set; }

    public string? FechaInicio { get; set; }

    public string? FechaFin { get; set; }

    public bool? Activo { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "usosMaximos debe ser mayor o igual a 1.")]
    public int? UsosMaximos { get; set; }
}

/// <summary>Body de <c>POST /codigos-descuento/validar</c> (público).</summary>
public sealed class ValidarCodigoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El código es obligatorio.")]
    public string Codigo { get; set; } = null!;

    public string? ProductoId { get; set; }
}

/// <summary>Producto al que aplica el código (proyección liviana).</summary>
public sealed record CodigoProductoResponse(string Id, string Nombre);

/// <summary>Representación de salida de un código de descuento.</summary>
public sealed record CodigoDescuentoResponse(
    string Id,
    string Codigo,
    string? Descripcion,
    string TipoDescuento,
    double Valor,
    string? ProductoId,
    CodigoProductoResponse? Producto,
    DateTime FechaInicio,
    DateTime FechaFin,
    bool Activo,
    int? UsosMaximos,
    int UsosActuales,
    DateTime CreatedAt);
