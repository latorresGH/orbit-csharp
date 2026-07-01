using Microsoft.EntityFrameworkCore;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Caja;

/// <summary>
/// Implementación de <see cref="ICajaService"/>. Réplica funcional del <c>registrarPagoPedido</c> y el batch
/// del NestJS. La ENTRADA y el update del pedido van en una sola transacción; el pre-chequeo de movimiento
/// duplicado (no anulado) evita cobrar dos veces el mismo pedido.
/// </summary>
public sealed class CajaService : ICajaService
{
    private readonly OrbitDbContext _db;

    public CajaService(OrbitDbContext db) => _db = db;

    public async Task<CajaMovimientoDto> RegistrarPagoPedidoAsync(
        string pedidoId,
        string confirmadoPor,
        string negocioId,
        double? gananciaRepartidor = null,
        MetodoPago? metodoPago = null,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // El Global Query Filter ya scopea al negocio; si el pedido es de otro negocio, no aparece → 404.
        var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == pedidoId, ct);
        if (pedido is null)
        {
            throw CajaException.NotFound("Pedido no encontrado");
        }
        if (pedido.Estado == EstadoPedido.CANCELADO)
        {
            throw CajaException.BadRequest("No se puede registrar pago de un pedido cancelado");
        }
        if (pedido.EstadoPago == EstadoPago.ANULADO)
        {
            throw CajaException.BadRequest("No se puede registrar pago de una cuenta anulada");
        }

        var yaTieneMovimiento = await _db.CajaMovimientos.AnyAsync(m => m.PedidoId == pedidoId && !m.Anulado, ct);
        if (yaTieneMovimiento)
        {
            throw CajaException.BadRequest("Este pedido ya tiene un movimiento de caja registrado");
        }

        var productosTotal = pedido.Total;
        var costoEnvio = pedido.CostoEnvio;
        var montoTotal = productosTotal + costoEnvio;

        var movimiento = new CajaMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            PedidoId = pedidoId,
            NegocioId = negocioId,
            Tipo = TipoMovimientoCaja.ENTRADA,
            MontoTotal = montoTotal,
            GananciaNegocio = productosTotal,
            GananciaRepartidor = gananciaRepartidor ?? costoEnvio,
            Descripcion = $"Pago registrado para pedido {pedidoId}",
            ConfirmadoPor = confirmadoPor,
            FechaConfirmacion = Now(),
            CreatedAt = Now(),
        };
        _db.CajaMovimientos.Add(movimiento);

        pedido.EstadoPago = EstadoPago.PAGADO;
        pedido.CuentaAbierta = false;
        if (metodoPago is { } mp)
        {
            pedido.MetodoPago = mp;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapMovimiento(movimiento);
    }

    public async Task<IReadOnlyList<ConfirmarResultado>> ConfirmarPagosPendientesAsync(
        IReadOnlyList<string> pedidoIds,
        string confirmadoPor,
        string negocioId,
        CancellationToken ct = default)
    {
        var resultados = new List<ConfirmarResultado>(pedidoIds.Count);
        foreach (var pedidoId in pedidoIds)
        {
            try
            {
                await RegistrarPagoPedidoAsync(pedidoId, confirmadoPor, negocioId, null, null, ct);
                resultados.Add(new ConfirmarResultado(pedidoId, true));
            }
            catch (CajaException ex)
            {
                resultados.Add(new ConfirmarResultado(pedidoId, false, ex.Message));
            }
        }

        return resultados;
    }

    private static CajaMovimientoDto MapMovimiento(CajaMovimiento m) => new(
        m.Id, m.PedidoId, m.Tipo, m.MontoTotal, m.GananciaNegocio, m.GananciaRepartidor,
        m.Descripcion, m.ConfirmadoPor, m.FechaConfirmacion, m.Anulado, m.CreatedAt);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
