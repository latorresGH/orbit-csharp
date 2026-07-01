namespace OrbIT.Api.Contracts.Config;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class SetConfigRequest
{
    public string Valor { get; set; } = null!;
    public string? Descripcion { get; set; }
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record ConfigItemResponse(string Id, string Clave, string Valor, string? Descripcion, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Respuesta de <c>GET /config/:clave</c> — valor null si la clave no está seteada.</summary>
public sealed record ConfigValorResponse(string Clave, string? Valor);

// ── Horario ──────────────────────────────────────────────────────────────────

public sealed record ProximaAperturaResponse(int Dia, string Hora, string DiaNombre);

public sealed record HorarioAbiertoResponse(
    bool Abierto,
    string? HoraApertura,
    string? HoraCierre,
    string HoraActual,
    IReadOnlyList<int> DiasAtencion,
    string Razon,
    ProximaAperturaResponse? ProximaApertura);
