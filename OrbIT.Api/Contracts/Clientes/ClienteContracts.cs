using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Clientes;

/// <summary>
/// Body para crear un cliente "plano" (alta manual desde el panel). El <c>NegocioId</c> NO viaja en el
/// body: se estampa en el servidor desde el tenant activo. Los totales arrancan en cero. La unicidad del
/// teléfono es por negocio (índice <c>Cliente_telefono_negocioId_key</c>).
/// </summary>
public sealed class CreateClienteRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    public string? Apellido { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "El teléfono es obligatorio.")]
    [StringLength(40, MinimumLength = 1, ErrorMessage = "El teléfono debe tener entre 1 y 40 caracteres.")]
    public string Telefono { get; set; } = null!;

    public string? DireccionFavorita { get; set; }

    public string? Notas { get; set; }
}

/// <summary>
/// Body del upsert por teléfono. Sirve a dos casos de uso con la misma lógica:
/// <list type="bullet">
///   <item><b>Alta/edición manual desde el panel:</b> sólo viajan datos de contacto; los totales no se tocan.</item>
///   <item><b>Flujo de pedidos</b> (futuro <c>PedidosController</c>): además viaja <see cref="MontoPedido"/>.
///   Cuando viene, el servidor acumula el pedido: <c>TotalPedidos++</c>, <c>TotalGastado += MontoPedido</c> y
///   <c>FechaUltimoPedido = ahora</c> (en el alta nueva, <c>TotalPedidos = 1</c>).</item>
/// </list>
/// El caller (PedidosController) es quien decide el monto; el cliente final nunca lo manda.
/// </summary>
public sealed class UpsertClienteRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    public string? Apellido { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "El teléfono es obligatorio.")]
    [StringLength(40, MinimumLength = 1, ErrorMessage = "El teléfono debe tener entre 1 y 40 caracteres.")]
    public string Telefono { get; set; } = null!;

    public string? DireccionFavorita { get; set; }

    public string? Notas { get; set; }

    /// <summary>
    /// Monto del pedido a acumular. Opcional: sólo lo manda el flujo de pedidos. Si viene (≥ 0) el servidor
    /// suma un pedido a los totales; si no viene, es un upsert de contacto y los totales quedan intactos.
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "El monto del pedido no puede ser negativo.")]
    public double? MontoPedido { get; set; }
}

/// <summary>
/// Body del update (PUT) de un cliente. Sólo datos de contacto editables manualmente; NO toca teléfono
/// (clave de unicidad/lookup) ni los totales (los maneja el flujo de pedidos). Semántica parcial estilo
/// PATCH, consistente con el resto del proyecto: sólo se modifican los campos presentes.
/// </summary>
public sealed class UpdateClienteRequest
{
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string? Nombre { get; set; }

    public string? Apellido { get; set; }

    public string? DireccionFavorita { get; set; }

    public string? Notas { get; set; }
}

/// <summary>
/// Representación de salida de un cliente (listado / create / upsert / update). Incluye dos campos
/// calculados sobre la marcha (sin columna ni query extra):
/// <list type="bullet">
///   <item><b>TicketPromedio:</b> <c>TotalGastado / TotalPedidos</c> (0 si no tiene pedidos).</item>
///   <item><b>EsClienteFrecuente:</b> <c>TotalPedidos &gt;= 5</c>.</item>
/// </list>
/// </summary>
public sealed record ClienteResponse(
    string Id,
    string Nombre,
    string? Apellido,
    string Telefono,
    string? DireccionFavorita,
    int TotalPedidos,
    double TotalGastado,
    double TicketPromedio,
    bool EsClienteFrecuente,
    DateTime? FechaUltimoPedido,
    string? Notas,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Detalle de un cliente con un preview embebido de sus pedidos más recientes (GET /clientes/{id}).</summary>
public sealed record ClienteDetalleResponse(
    string Id,
    string Nombre,
    string? Apellido,
    string Telefono,
    string? DireccionFavorita,
    int TotalPedidos,
    double TotalGastado,
    double TicketPromedio,
    bool EsClienteFrecuente,
    DateTime? FechaUltimoPedido,
    string? Notas,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PedidoPreviewResponse> PedidosRecientes);

/// <summary>Proyección liviana de un pedido para el historial/preview del cliente.</summary>
public sealed record PedidoPreviewResponse(
    string Id,
    EstadoPedido Estado,
    double Total,
    string? Direccion,
    DateTime CreatedAt);

/// <summary>Página de clientes: <c>{ data, total }</c> (paginación server-side).</summary>
public sealed record ClientesPageResponse(
    IReadOnlyList<ClienteResponse> Data,
    int Total);

/// <summary>Página de pedidos de un cliente: <c>{ data, total }</c> (paginación server-side).</summary>
public sealed record PedidosPageResponse(
    IReadOnlyList<PedidoPreviewResponse> Data,
    int Total);

/// <summary>Estadísticas del CRM (GET /clientes/stats).</summary>
public sealed record ClientesStatsResponse(
    int Total,
    int ConMasDeUnPedido,
    IReadOnlyList<ClienteResponse> TopClientes);
