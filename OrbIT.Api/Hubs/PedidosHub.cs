using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace OrbIT.Api.Hubs;

/// <summary>
/// Hub de pedidos en tiempo real (equivalente al <c>PedidosGateway</c> de socket.io del NestJS). Ruta
/// <c>/hubs/pedidos</c>. El server emite <c>nuevo-pedido</c> a la room del negocio cuando entra un pedido
/// desde el menú público.
///
/// Autenticación en el handshake (divergencia deliberada respecto al NestJS, que autenticaba recién en el
/// mensaje 'join'): el <c>[Authorize]</c> exige JWT válido para conectar. El token viaja en la cookie
/// <c>access_token</c> que el navegador manda automáticamente en el handshake (negotiate + WebSocket), y lo
/// valida el mismo <c>JwtBearer.OnMessageReceived</c> configurado en Program.cs. Sin JWT válido → 401, la
/// conexión ni se abre. Al conectar, el cliente se une automáticamente a la room = su <c>negocioId</c> (claim),
/// así que no necesita mandar ningún 'join' explícito.
/// </summary>
[Authorize]
public sealed class PedidosHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var negocioId = Context.User?.FindFirst("negocioId")?.Value;

        // Un principal autenticado sin negocioId (ej. SUPERADMIN) no tiene room propia: se rechaza la conexión.
        if (string.IsNullOrEmpty(negocioId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, negocioId);
        await base.OnConnectedAsync();
    }
}
