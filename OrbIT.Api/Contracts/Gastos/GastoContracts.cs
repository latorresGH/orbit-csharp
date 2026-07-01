using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Gastos;

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed class CreateGastoRequest
{
    [Required(AllowEmptyStrings = false)]
    public string Categoria { get; set; } = null!;

    public double Monto { get; set; }

    public string? Descripcion { get; set; }

    /// <summary>Fecha del gasto (ISO). Si se omite, se usa el momento actual.</summary>
    public string? Fecha { get; set; }

    public string? ComprobanteUrl { get; set; }

    public string? CreadoPor { get; set; }
}

public sealed class UpdateGastoRequest
{
    public string? Categoria { get; set; }
    public double? Monto { get; set; }
    public string? Descripcion { get; set; }
    public string? Fecha { get; set; }
    public string? ComprobanteUrl { get; set; }
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record GastoResponse(
    string Id,
    string Categoria,
    double Monto,
    string? Descripcion,
    DateTime Fecha,
    string? ComprobanteUrl,
    string? CreadoPor,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Listado paginado — <c>{ data, total }</c>, paridad con el NestJS.</summary>
public sealed record GastosPagedResponse(IReadOnlyList<GastoResponse> Data, int Total);

public sealed record GastoResumenCategoria(string Categoria, double Total, int Cantidad);

public sealed record GastoResumenResponse(double Total, int Cantidad, IReadOnlyList<GastoResumenCategoria> PorCategoria);
