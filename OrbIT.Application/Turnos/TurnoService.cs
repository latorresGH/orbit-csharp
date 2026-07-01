using Microsoft.EntityFrameworkCore;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Turnos;

/// <summary>
/// Implementación de <see cref="ITurnoService"/>. Réplica funcional del <c>abrir</c>/<c>cerrar</c> de NestJS
/// con dos cambios: (1) el turno es global por negocio (ver <see cref="ITurnoService"/>), y (2) la apertura es
/// transaccional igual que el cierre. El cálculo de ventas/efectivo esperado se comparte entre el cierre y la
/// consulta del turno activo (<see cref="CalcularAsync"/>).
/// </summary>
public sealed class TurnoService : ITurnoService
{
    /// <summary>Umbral de alerta por faltante de caja (mismo valor que el NestJS): diferencia menor a −$100.</summary>
    private const double UmbralAlerta = -100;

    private readonly OrbitDbContext _db;

    public TurnoService(OrbitDbContext db) => _db = db;

    // ═════════════════════════════════════════════════════════════════════════
    // ABRIR
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<TurnoDto> AbrirTurnoAsync(string userId, double montoInicial, string? notas, string negocioId, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Guard: un único turno activo por negocio (no por usuario). El Global Query Filter ya scopea al
        // negocio del request. Hay una ventana TOCTOU teórica (como en el NestJS): sin un índice único parcial
        // sobre horaFin=null no se puede cerrar del todo; se documenta como limitación conocida.
        if (await _db.Turnos.AnyAsync(t => t.HoraFin == null, ct))
        {
            throw TurnoException.BadRequest("Ya hay un turno abierto en el negocio. Cerralo antes de abrir uno nuevo.");
        }

        var now = Now();
        var turno = new Turno
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            UserId = userId,
            Tipo = TipoTurno.APERTURA,
            HoraInicio = now,
            CajaAperturaMonto = montoInicial,
            Notas = TrimOrNull(notas),
            CreatedAt = now,
        };
        _db.Turnos.Add(turno);

        _db.CajaMovimientos.Add(new CajaMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            Tipo = TipoMovimientoCaja.APERTURA_TURNO,
            MontoTotal = montoInicial,
            GananciaNegocio = 0,
            GananciaRepartidor = 0,
            Descripcion = $"Apertura de turno — fondo inicial ${montoInicial}",
            ConfirmadoPor = userId,
            FechaConfirmacion = now,
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var nombre = await NombreUsuarioAsync(userId, ct);
        return MapTurno(turno, nombre);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CERRAR
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<CierreTurnoResult> CerrarTurnoAsync(string userId, double montoFinal, string? notas, string negocioId, CancellationToken ct = default)
    {
        var turno = await _db.Turnos
            .OrderByDescending(t => t.HoraInicio)
            .FirstOrDefaultAsync(t => t.HoraFin == null, ct);
        if (turno is null)
        {
            throw TurnoException.BadRequest("No hay ningún turno abierto para cerrar.");
        }

        var horaFin = Now();

        // Ventas del turno (todas las formas de pago) y efectivo esperado (solo efectivo + entradas − salidas).
        var (ventas, efectivo) = await CalcularAsync(turno.HoraInicio, horaFin, ct);
        var esperado = turno.CajaAperturaMonto + efectivo;
        var diferencia = montoFinal - esperado;

        var totalMovimientos = await _db.CajaMovimientos.CountAsync(
            m => m.Tipo == TipoMovimientoCaja.ENTRADA && !m.Anulado
                && m.FechaConfirmacion >= turno.HoraInicio && m.FechaConfirmacion <= horaFin,
            ct);

        var notasCierre = TrimOrNull(notas);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        turno.HoraFin = horaFin;
        turno.CajaCierreMonto = montoFinal;
        turno.VentasTotales = ventas;
        turno.MontoEsperado = esperado;
        turno.Notas = notasCierre is null
            ? turno.Notas
            : $"{turno.Notas ?? string.Empty}\n[Cierre]: {notasCierre}".Trim();

        _db.CajaMovimientos.Add(new CajaMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            Tipo = TipoMovimientoCaja.CIERRE_TURNO,
            MontoTotal = montoFinal,
            GananciaNegocio = 0,
            GananciaRepartidor = 0,
            Descripcion = $"Cierre de turno — {totalMovimientos} movimientos — ventas ${ventas}",
            ConfirmadoPor = userId,
            FechaConfirmacion = horaFin,
            CreatedAt = horaFin,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var nombre = await NombreUsuarioAsync(turno.UserId, ct);
        return new CierreTurnoResult(MapTurno(turno, nombre), esperado, diferencia, diferencia < UmbralAlerta);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ACTIVO (lectura con cálculo en vivo)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<TurnoActivoResult?> ObtenerTurnoActivoAsync(string negocioId, CancellationToken ct = default)
    {
        var turno = await _db.Turnos
            .OrderByDescending(t => t.HoraInicio)
            .FirstOrDefaultAsync(t => t.HoraFin == null, ct);
        if (turno is null)
        {
            return null;
        }

        // Turno abierto: sin cota superior, se cuenta hasta el momento de la consulta.
        var (ventas, efectivo) = await CalcularAsync(turno.HoraInicio, null, ct);
        var nombre = await NombreUsuarioAsync(turno.UserId, ct);
        return new TurnoActivoResult(MapTurno(turno, nombre), ventas, turno.CajaAperturaMonto + efectivo);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Cálculo de ventas / efectivo esperado
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ventas del turno y efectivo esperado en el rango <c>[horaInicio, horaFin]</c> (horaFin null = sin cota
    /// superior, para el turno en curso). Excluye movimientos anulados.
    ///   • ventas = Σ gananciaNegocio de ENTRADAS (todas las formas de pago).
    ///   • efectivo = ventas cobradas en EFECTIVO (excluyendo DELIVERY: ese efectivo lo rinde el repartidor
    ///     aparte) + entradas manuales sin pedido − salidas.
    /// Los pagos MITAD_Y_MITAD no se cuentan como efectivo (no hay desglose persistido): limitación conocida,
    /// idéntica al NestJS. Las cuatro agregaciones van secuenciales (un solo DbContext no admite queries
    /// concurrentes; el NestJS las corría en Promise.all sobre conexiones distintas).
    /// </summary>
    private async Task<(double Ventas, double Efectivo)> CalcularAsync(DateTime horaInicio, DateTime? horaFin, CancellationToken ct)
    {
        var movs = _db.CajaMovimientos.Where(m => !m.Anulado && m.FechaConfirmacion >= horaInicio);
        if (horaFin is { } hf)
        {
            movs = movs.Where(m => m.FechaConfirmacion <= hf);
        }

        var ventas = await movs
            .Where(m => m.Tipo == TipoMovimientoCaja.ENTRADA)
            .SumAsync(m => m.GananciaNegocio, ct);

        var entradasEfectivo = await movs
            .Where(m => m.Tipo == TipoMovimientoCaja.ENTRADA
                && m.Pedido != null
                && m.Pedido.MetodoPago == MetodoPago.EFECTIVO
                && m.Pedido.Tipo != TipoPedido.DELIVERY)
            .SumAsync(m => m.GananciaNegocio, ct);

        var entradasManuales = await movs
            .Where(m => m.Tipo == TipoMovimientoCaja.ENTRADA && m.PedidoId == null)
            .SumAsync(m => m.MontoTotal, ct);

        var salidas = await movs
            .Where(m => m.Tipo == TipoMovimientoCaja.SALIDA)
            .SumAsync(m => m.MontoTotal, ct);

        return (ventas, entradasEfectivo + entradasManuales - salidas);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private Task<string?> NombreUsuarioAsync(string userId, CancellationToken ct) =>
        _db.Users.Where(u => u.Id == userId).Select(u => (string?)u.Nombre).FirstOrDefaultAsync(ct);

    private static TurnoDto MapTurno(Turno t, string? nombre) => new(
        t.Id, t.UserId, nombre, t.HoraInicio, t.HoraFin, t.CajaAperturaMonto,
        t.CajaCierreMonto, t.VentasTotales, t.MontoEsperado, t.Notas, t.CreatedAt);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static string? TrimOrNull(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }
}
