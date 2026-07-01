using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Turnos;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class AbrirTurnoRequest
{
    [Range(0, double.MaxValue, ErrorMessage = "El monto inicial no puede ser negativo.")]
    public double MontoInicial { get; set; }

    public string? Notas { get; set; }
}

public sealed class CerrarTurnoRequest
{
    [Range(0, double.MaxValue, ErrorMessage = "El monto final no puede ser negativo.")]
    public double MontoFinal { get; set; }

    public string? Notas { get; set; }
}

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>Turno para las respuestas de abrir / historial.</summary>
public sealed record TurnoResponse(
    string Id,
    string UserId,
    string? UserNombre,
    DateTime HoraInicio,
    DateTime? HoraFin,
    double CajaAperturaMonto,
    double? CajaCierreMonto,
    double VentasTotales,
    double? MontoEsperado,
    double? Diferencia,
    bool AlertaDiferencia,
    string? Notas,
    DateTime CreatedAt);

/// <summary>Turno activo del negocio + métricas en vivo.</summary>
public sealed record TurnoActivoResponse(
    string Id,
    string UserId,
    string? UserNombre,
    DateTime HoraInicio,
    double CajaAperturaMonto,
    double VentasEnVivo,
    double MontoEsperado,
    string? Notas,
    DateTime CreatedAt);

/// <summary>Cierre: el turno + la diferencia calculada.</summary>
public sealed record CierreTurnoResponse(
    TurnoResponse Turno,
    double MontoEsperado,
    double Diferencia,
    bool AlertaDiferencia);

// ── Stats ────────────────────────────────────────────────────────────────────

public sealed record TurnoStatsUsuario(string UserId, string? Nombre, int Turnos, double VentasTotales, double Diferencias);

public sealed record TurnoStatsResponse(
    int TotalTurnos,
    int ConDiferenciaNegativa,
    double DiferenciaTotal,
    double VentasTotales,
    IReadOnlyList<TurnoStatsUsuario> PorUsuario);
