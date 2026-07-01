namespace OrbIT.Application.Turnos;

// ── Resultados (la Api mapea estos records a sus responses; Application no depende de los contracts de Api) ──

/// <summary>Snapshot de un turno para devolver al cliente. <see cref="UserNombre"/> es quién lo abrió.</summary>
public sealed record TurnoDto(
    string Id,
    string UserId,
    string? UserNombre,
    DateTime HoraInicio,
    DateTime? HoraFin,
    double CajaAperturaMonto,
    double? CajaCierreMonto,
    double VentasTotales,
    double? MontoEsperado,
    string? Notas,
    DateTime CreatedAt);

/// <summary>Turno activo del negocio + métricas en vivo (ventas del turno y efectivo esperado a la fecha).</summary>
public sealed record TurnoActivoResult(TurnoDto Turno, double VentasEnVivo, double MontoEsperado);

/// <summary>
/// Resultado del cierre: el turno ya persistido + la diferencia calculada (efectivo contado − esperado).
/// <see cref="AlertaDiferencia"/> replica el umbral del NestJS (faltante mayor a $100).
/// </summary>
public sealed record CierreTurnoResult(TurnoDto Turno, double MontoEsperado, double Diferencia, bool AlertaDiferencia);

/// <summary>
/// Excepción de dominio del flujo de turnos. El controller la mapea a la respuesta HTTP correspondiente
/// (400/404), replicando los <c>BadRequestException</c> / <c>NotFoundException</c> de NestJS sin acoplar
/// Application a ASP.NET. Mismo patrón que <c>PedidoException</c>.
/// </summary>
public sealed class TurnoException : Exception
{
    public int StatusCode { get; }

    private TurnoException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public static TurnoException BadRequest(string message) => new(400, message);

    public static TurnoException NotFound(string message) => new(404, message);
}

/// <summary>
/// Orquestador de las operaciones transaccionales del módulo Turnos: apertura y cierre de la caja del
/// negocio. Ambas escriben un <c>Turno</c> y su <c>CajaMovimiento</c> (APERTURA_TURNO / CIERRE_TURNO) en una
/// sola transacción. El historial y las stats (solo lectura) viven en el controller.
///
/// <para><b>Divergencia deliberada respecto al NestJS: el turno es GLOBAL por negocio, no por usuario.</b>
/// NestJS abría un turno por <c>(negocioId, userId)</c> — varias "cajas" simultáneas, una por empleado, lo
/// que es un error conceptual: la caja es del local, no de cada persona. Acá hay <b>un único turno activo por
/// negocio</b>; todos los empleados operan dentro de él. <c>userId</c> se conserva como "quién abrió el turno"
/// (dato histórico), pero la clave de unicidad es solo <c>negocioId</c>.</para>
/// </summary>
public interface ITurnoService
{
    /// <summary>
    /// Abre el turno del negocio. Falla con 400 si ya hay uno activo (<c>horaFin == null</c>) en el negocio.
    /// Crea el <c>Turno</c> y el <c>CajaMovimiento</c> APERTURA_TURNO en una sola transacción (mejora sobre el
    /// NestJS, que los creaba sueltos y podía dejar un turno sin su movimiento de apertura).
    /// </summary>
    Task<TurnoDto> AbrirTurnoAsync(string userId, double montoInicial, string? notas, string negocioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cierra el turno activo del negocio. Calcula ventas del turno, efectivo esperado y diferencia contra el
    /// <paramref name="montoFinal"/> contado. Persiste el cierre y su <c>CajaMovimiento</c> CIERRE_TURNO en una
    /// sola transacción. Lo puede cerrar cualquier ADMIN, no solo quien lo abrió.
    /// </summary>
    Task<CierreTurnoResult> CerrarTurnoAsync(string userId, double montoFinal, string? notas, string negocioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turno activo del negocio con ventas y efectivo esperado calculados en vivo (hasta el momento de la
    /// consulta). Devuelve <c>null</c> si no hay ninguno abierto.
    /// </summary>
    Task<TurnoActivoResult?> ObtenerTurnoActivoAsync(string negocioId, CancellationToken cancellationToken = default);
}
