namespace OrbIT.Application.Demora;

/// <summary>
/// Stub de <see cref="IDemoraService"/> que siempre devuelve <c>null</c>. Tanda A: el pedido se crea con
/// <c>demoraEstimadaMin = null</c>, comportamiento idéntico al <c>catch</c> best-effort del NestJS.
///
/// TODO (módulo Demora completo): reemplazar por una implementación que replique
/// <c>DemoraService.calcularTiempoEstimadoPedido</c> de NestJS:
///   1. Cargar los productos (tenant-scoped) y tomar el MAX de <c>TiempoPreparacionMin</c> (null → 0).
///   2. Leer <c>DemoraConfig</c> del negocio; si no existe o no está activo → demora extra 0.
///   3. Si modo MANUAL → usar <c>ValorManual</c>; si AUTO → contar pedidos activos
///      (estado PENDIENTE/EN_PREPARACION) y mapear contra los rangos (jsonb <c>Rangos</c> o RANGOS_DEFAULT).
///   4. Devolver <c>tiempoPreparacionMax + demoraExtra</c>.
/// La entidad <c>DemoraConfig</c> ya está en el scaffold; falta el controller/servicio del módulo Demora.
/// </summary>
public sealed class DemoraServiceStub : IDemoraService
{
    public Task<int?> CalcularDemoraEstimadaAsync(
        IReadOnlyList<string> productoIds,
        string negocioId,
        CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
}
