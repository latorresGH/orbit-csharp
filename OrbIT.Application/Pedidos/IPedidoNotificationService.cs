namespace OrbIT.Application.Pedidos;

/// <summary>
/// Payload del evento <c>nuevo-pedido</c> que se emite por SignalR a la room del negocio. Réplica exacta del
/// objeto que el gateway NestJS mandaba por socket.io (mismas claves, en camelCase sobre el cable). Los
/// opcionales del original (<c>apellidoCliente</c>, <c>numeroCliente</c>) viajan como string vacío, igual que
/// en NestJS (<c>pedido.apellidoCliente || ''</c>).
/// </summary>
public sealed record NuevoPedidoNotification(
    string Id,
    string NombreCliente,
    string ApellidoCliente,
    string NumeroCliente,
    string Tipo,
    double Total,
    string Timestamp);

/// <summary>
/// Payload del evento <c>pedido-actualizado</c> que se emite por SignalR a la room del negocio cuando cambia
/// el estado de un pedido (PATCH /pedidos/{id}/estado). Permite al panel refrescar la tarjeta sin recargar el
/// listado. Estados en camelCase-string sobre el cable (el enum se serializa con <c>.ToString()</c>, igual que
/// <c>Tipo</c> en <see cref="NuevoPedidoNotification"/>); <c>timestamp</c> replica el
/// <c>new Date().toISOString()</c> del original.
/// </summary>
public sealed record PedidoActualizadoNotification(
    string Id,
    string Estado,
    string EstadoAnterior,
    string Timestamp);

/// <summary>
/// Abstracción de notificación en tiempo real de pedidos. Vive en Application para que <c>PedidoService</c>
/// pueda emitir sin depender de la capa Api (donde vive el <c>PedidosHub</c> y <c>IHubContext</c>): la
/// implementación concreta (<c>PedidosNotificationService</c>) se registra en OrbIT.Api y envuelve SignalR.
/// Es best-effort: una falla al notificar nunca debe romper la creación del pedido (que ya está commiteada).
/// </summary>
public interface IPedidoNotificationService
{
    /// <summary>Emite <c>nuevo-pedido</c> a la room = <paramref name="negocioId"/>. No lanza (best-effort).</summary>
    Task NotificarNuevoPedidoAsync(string negocioId, NuevoPedidoNotification pedido, CancellationToken cancellationToken = default);

    /// <summary>Emite <c>pedido-actualizado</c> a la room = <paramref name="negocioId"/>. No lanza (best-effort).</summary>
    Task NotificarActualizacionAsync(string negocioId, PedidoActualizadoNotification payload, CancellationToken cancellationToken = default);
}
