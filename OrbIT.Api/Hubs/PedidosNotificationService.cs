using Microsoft.AspNetCore.SignalR;
using OrbIT.Application.Pedidos;

namespace OrbIT.Api.Hubs;

/// <summary>
/// Implementación de <see cref="IPedidoNotificationService"/> sobre SignalR. Vive en la capa Api (donde está
/// el <see cref="PedidosHub"/> y disponible <see cref="IHubContext{THub}"/>). Emite el evento a la room del
/// negocio. Best-effort: cualquier excepción se loguea y se traga — el pedido ya fue creado y commiteado, una
/// falla de notificación no debe propagarse al flujo de creación.
/// </summary>
public sealed class PedidosNotificationService : IPedidoNotificationService
{
    private const string EventoNuevoPedido = "nuevo-pedido";

    private readonly IHubContext<PedidosHub> _hub;
    private readonly ILogger<PedidosNotificationService> _logger;

    public PedidosNotificationService(IHubContext<PedidosHub> hub, ILogger<PedidosNotificationService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotificarNuevoPedidoAsync(string negocioId, NuevoPedidoNotification pedido, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.Group(negocioId).SendAsync(EventoNuevoPedido, pedido, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error emitiendo '{Evento}' a la room {NegocioId} (pedido {PedidoId})",
                EventoNuevoPedido, negocioId, pedido.Id);
        }
    }
}
