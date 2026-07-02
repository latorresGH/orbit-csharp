using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Insumos;

/// <summary>
/// Body para crear un insumo. El <c>NegocioId</c> NO viaja en el body: se estampa en el servidor desde
/// el tenant activo. <c>StockInicial</c> mapea a <c>StockActual</c>; el <c>StockMinimo</c> queda en el
/// default de la base (5.0) — paridad con NestJS, que no lo recibía en el create.
/// </summary>
public sealed class CreateInsumoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El stock inicial no puede ser negativo.")]
    public double StockInicial { get; set; }

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida UnidadMedida { get; set; } = UnidadMedida.UNIDAD;

    /// <summary>Proveedor opcional. Si se indica, se valida que pertenezca al negocio activo.</summary>
    public string? ProveedorId { get; set; }
}

/// <summary>
/// Body para actualizar un insumo. Semántica PATCH parcial: sólo se modifican los campos presentes.
/// <see cref="ProveedorId"/> tiene semántica de tres estados: ausente/<c>null</c> = no tocar;
/// cadena vacía = desvincular el proveedor; valor = vincular (validado contra el tenant).
/// Si cambia <see cref="StockActual"/> se registra un <c>StockMovimiento</c> de ajuste manual.
/// </summary>
public sealed class UpdateInsumoRequest
{
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string? Nombre { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El stock mínimo no puede ser negativo.")]
    public double? StockMinimo { get; set; }

    [EnumDataType(typeof(UnidadMedida), ErrorMessage = "Unidad de medida inválida.")]
    public UnidadMedida? UnidadMedida { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El stock no puede ser negativo.")]
    public double? StockActual { get; set; }

    public string? ProveedorId { get; set; }

    public bool? Activo { get; set; }
}

/// <summary>Body para sumar o descontar stock de un insumo. La cantidad debe ser &gt; 0.</summary>
public sealed class InsumoStockMovRequest
{
    [Range(0.0001, double.MaxValue, ErrorMessage = "Cantidad inválida")]
    public double Cantidad { get; set; }

    public string? Motivo { get; set; }
}

/// <summary>Body para activar/desactivar un insumo (baja/alta lógica).</summary>
public sealed class SetInsumoActivoRequest
{
    public bool Activo { get; set; }
}

/// <summary>Representación de salida de un insumo con su proveedor (si tiene).</summary>
public sealed record InsumoResponse(
    string Id,
    string Nombre,
    UnidadMedida UnidadMedida,
    double StockActual,
    double StockMinimo,
    bool Activo,
    string? ProveedorId,
    string? ProveedorNombre,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Disponibilidad de un producto según el stock de insumos (cruce receta × stockActual). Sólo expone
/// el booleano: pensado para el menú público, no revela cantidades ni inventario.
/// </summary>
public sealed record DisponibilidadProductoResponse(string ProductoId, bool Disponible);

/// <summary>Movimiento de stock (lectura). Incluye el nombre del insumo en los listados que lo traen.</summary>
public sealed record StockMovimientoResponse(
    string Id,
    string? InsumoId,
    string? ExtraId,
    string? AderezoId,
    string Tipo,
    double Cantidad,
    double StockAntes,
    double StockDespues,
    string? PedidoId,
    string? Motivo,
    string? UserId,
    DateTime CreatedAt,
    string? InsumoNombre);

/// <summary>Página de resultados (data + metadatos de paginación), paridad con el shape de NestJS.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Data, int Total, int Page, int TotalPages);

// ── Tanda B: movimientos unificados (stock + turnos) ─────────────────────────

/// <summary>Datos del insumo embebidos en una fila de movimiento de stock (los turnos lo traen en null).</summary>
public sealed record MovimientoUnificadoInsumo(string Nombre, UnidadMedida UnidadMedida);

/// <summary>
/// Fila del reporte unificado <c>GET /insumos/movimientos</c>: forma común que mezcla movimientos de stock
/// con eventos de turno (apertura/cierre). Los campos de caja (<see cref="MontoReal"/>,
/// <see cref="MontoEsperado"/>, <see cref="Diferencia"/>) sólo vienen en las filas de turno; los de stock
/// (cantidad, stockAntes/Despues, insumo/extra/aderezo) sólo en las de stock. Paridad de shape con NestJS.
/// </summary>
public sealed record MovimientoUnificadoResponse(
    string Id,
    string Tipo,
    DateTime CreatedAt,
    string? InsumoId,
    string? ExtraId,
    string? AderezoId,
    double Cantidad,
    double StockAntes,
    double StockDespues,
    string? PedidoId,
    string? Motivo,
    string? UserId,
    string? ConfirmadoPor,
    MovimientoUnificadoInsumo? Insumo,
    string? ExtraNombre,
    string? AderezoNombre,
    double? MontoReal,
    double? MontoEsperado,
    double? Diferencia);

// ── Tanda B: reporte de consumo ──────────────────────────────────────────────

/// <summary>
/// Fila del reporte de consumo por período (<c>GET /insumos/reporte/consumo</c>): consumo total por insumo
/// vía movimientos <c>DESCUENTO_PEDIDO</c>. <see cref="UnidadMedida"/> es null si el insumo fue eliminado
/// (en NestJS venía como cadena vacía).
/// </summary>
public sealed record ReporteConsumoItemResponse(
    string InsumoId,
    string Nombre,
    UnidadMedida? UnidadMedida,
    double TotalConsumido,
    int CantidadMovimientos);

// ── Tanda B: disponibilidad real de extras/aderezos ──────────────────────────

/// <summary>
/// Body de <c>POST /insumos/disponibilidad</c>. NO lleva <c>negocioId</c> (a diferencia del NestJS, que lo
/// recibía en el body por error de diseño): el tenant se resuelve por claim o por <c>?negocio=slug</c> vía
/// <c>[AllowAnonymousWithTenant]</c>.
/// </summary>
public sealed class CheckDisponibilidadRequest
{
    public List<string> ExtraIds { get; set; } = new();
    public List<string> AderezoIds { get; set; } = new();
    public string? CategoriaId { get; set; }
}

/// <summary>Disponibilidad real de un extra o aderezo según stock e insumo/consumo por categoría.</summary>
public sealed record DisponibilidadItemResponse(
    string Id,
    string Nombre,
    string Tipo,
    double StockActual,
    double ConsumoPorUnidad,
    int Disponible);
