using System.ComponentModel.DataAnnotations;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Contracts.Plano;

/// <summary>
/// Body del upsert del plano (PUT /plano): reemplaza por completo el layout del negocio. Los elementos usan
/// el mismo shape <see cref="ElementoPlano"/> que se persiste en la columna jsonb (evita un DTO paralelo
/// idéntico). <c>CanvasWidth</c>/<c>CanvasHeight</c> ≤ 0 se normalizan a los defaults (1200×800) en el
/// controller. El <c>NegocioId</c> no viaja en el body: se estampa desde el tenant activo.
/// </summary>
public sealed class SavePlanoRequest
{
    public List<ElementoPlano> Elementos { get; set; } = new();

    public int CanvasWidth { get; set; }

    public int CanvasHeight { get; set; }
}

/// <summary>Representación de salida del plano (GET / PUT / DELETE).</summary>
public sealed record PlanoResponse(
    string Id,
    string NegocioId,
    IReadOnlyList<ElementoPlano> Elementos,
    int CanvasWidth,
    int CanvasHeight,
    DateTime CreatedAt,
    DateTime UpdatedAt);
