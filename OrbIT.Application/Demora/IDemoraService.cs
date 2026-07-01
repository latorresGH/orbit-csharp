namespace OrbIT.Application.Demora;

/// <summary>
/// Cálculo del tiempo estimado de demora de un pedido. Lo consume <c>CrearPedidoAsync</c> de forma
/// <b>best-effort</b>: si devuelve <c>null</c>, el pedido se crea igual con <c>demoraEstimadaMin = null</c>
/// (idéntico al <c>try/catch</c> del NestJS).
/// </summary>
public interface IDemoraService
{
    /// <summary>
    /// Devuelve la demora estimada total en minutos, o <c>null</c> si no se puede calcular.
    /// </summary>
    Task<int?> CalcularDemoraEstimadaAsync(
        IReadOnlyList<string> productoIds,
        string negocioId,
        CancellationToken cancellationToken = default);
}
