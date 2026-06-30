using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Mesas;

/// <summary>
/// Body para crear una mesa. El <c>NegocioId</c> NO viaja en el body: se estampa en el servidor desde el
/// tenant activo. La unicidad del número es por negocio (índice <c>Mesa_numero_negocioId_key</c>). Las
/// posiciones se validan contra la grilla del negocio (<c>posX &lt; cols</c>, <c>posY &lt; rows</c>).
/// </summary>
public sealed class CreateMesaRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "El número de mesa debe ser mayor o igual a 1.")]
    public int Numero { get; set; }

    public string? Nombre { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "La capacidad debe ser mayor o igual a 1.")]
    public int? Capacidad { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "posX no puede ser negativo.")]
    public int? PosX { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "posY no puede ser negativo.")]
    public int? PosY { get; set; }

    public bool? Activa { get; set; }
}

/// <summary>
/// Body para actualizar una mesa (datos estructurales, ADMIN). Semántica PATCH parcial: sólo se modifican
/// los campos presentes. Para el cambio de estado/cuenta usar <see cref="UpdateMesaEstadoRequest"/>.
/// </summary>
public sealed class UpdateMesaRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "El número de mesa debe ser mayor o igual a 1.")]
    public int? Numero { get; set; }

    public string? Nombre { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "La capacidad debe ser mayor o igual a 1.")]
    public int? Capacidad { get; set; }

    public EstadoMesa? Estado { get; set; }

    public bool? Activa { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "posX no puede ser negativo.")]
    public int? PosX { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "posY no puede ser negativo.")]
    public int? PosY { get; set; }
}

/// <summary>
/// Body para cambiar el estado de una mesa (ADMIN+TRABAJADOR). <c>OCUPADA</c> exige
/// <see cref="PedidoActivoId"/>; pasar a <c>LIBRE</c> con un pedido activo presente se rechaza (hay que
/// liberar primero).
/// </summary>
public sealed class UpdateMesaEstadoRequest
{
    [Required(ErrorMessage = "El estado es obligatorio.")]
    public EstadoMesa Estado { get; set; }

    public string? PedidoActivoId { get; set; }
}

/// <summary>Body para actualizar la grilla del salón (ADMIN). Claves <c>mesas_grid_cols/rows</c> en Configuracion.</summary>
public sealed class UpdateGridConfigRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "Las columnas deben ser mayor o igual a 1.")]
    public int Cols { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Las filas deben ser mayor o igual a 1.")]
    public int Rows { get; set; }
}

/// <summary>Representación de salida de una mesa (create / update / estado / liberar).</summary>
public sealed record MesaResponse(
    string Id,
    int Numero,
    string? Nombre,
    EstadoMesa Estado,
    int Capacidad,
    bool Activa,
    int PosX,
    int PosY,
    string? PedidoActivoId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Mesa para el tablero (GET /mesas): incluye un resumen liviano del pedido activo con el conteo de ítems
/// (sin traer los detalles completos — proyección correlacionada).
/// </summary>
public sealed record MesaTableroResponse(
    string Id,
    int Numero,
    string? Nombre,
    EstadoMesa Estado,
    int Capacidad,
    bool Activa,
    int PosX,
    int PosY,
    PedidoActivoResumen? PedidoActivo);

/// <summary>Resumen del pedido activo para el tablero (conteo de ítems en vez de los detalles).</summary>
public sealed record PedidoActivoResumen(
    string Id,
    double Total,
    EstadoPedido Estado,
    bool CuentaAbierta,
    int CantidadItems,
    DateTime CreatedAt);

/// <summary>Detalle de una mesa (GET /mesas/{id}) con el pedido activo y sus ítems (proyección liviana).</summary>
public sealed record MesaDetalleResponse(
    string Id,
    int Numero,
    string? Nombre,
    EstadoMesa Estado,
    int Capacidad,
    bool Activa,
    int PosX,
    int PosY,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    PedidoActivoDetalle? PedidoActivo);

/// <summary>Pedido activo con sus ítems (proyección liviana para el detalle de la mesa).</summary>
public sealed record PedidoActivoDetalle(
    string Id,
    double Total,
    EstadoPedido Estado,
    bool CuentaAbierta,
    IReadOnlyList<PedidoActivoItem> Detalles);

/// <summary>Ítem del pedido activo (id, cantidad, subtotal, nombre del producto).</summary>
public sealed record PedidoActivoItem(
    string Id,
    int Cantidad,
    double Subtotal,
    string? NombreProducto);

/// <summary>Configuración de la grilla del salón.</summary>
public sealed record GridConfigResponse(int Cols, int Rows);
