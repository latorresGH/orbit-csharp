using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.ToppingGrupos;

/// <summary>
/// Body para crear un grupo de toppings. Paridad con NestJS: sólo <c>Nombre</c> es obligatorio,
/// el resto tiene defaults. NO se valida unicidad de nombre (el sistema permite repetidos) y el
/// <c>NegocioId</c> no viaja en el body: se estampa en el servidor desde el tenant activo.
/// </summary>
public sealed class CreateToppingGrupoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    /// <summary>
    /// Cantidad de Extras de ESTE grupo que el cliente puede elegir sin costo. Es la gratuidad a
    /// nivel grupo de toppings, distinta de <c>Categoria.MaxAderezosGratis</c> (que aplica a los
    /// Aderezos de una categoría completa). Default 3 (igual que la columna en la base).
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "El máximo de extras gratis no puede ser negativo.")]
    public int MaxExtrasGratis { get; set; } = 3;

    /// <summary>Si el grupo viene incluido por defecto en el producto. Default true.</summary>
    public bool EsIncluido { get; set; } = true;

    [Range(0, int.MaxValue, ErrorMessage = "El orden no puede ser negativo.")]
    public int Orden { get; set; }
}

/// <summary>
/// Body para actualizar un grupo de toppings (reemplazo total de los campos editables).
/// Igual que en el create, no se valida unicidad de nombre.
/// </summary>
public sealed class UpdateToppingGrupoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    /// <summary>
    /// Máximo de Extras gratis de este grupo. SOLO aplica a los Extras del grupo
    /// (ver <see cref="CreateToppingGrupoRequest.MaxExtrasGratis"/>).
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "El máximo de extras gratis no puede ser negativo.")]
    public int MaxExtrasGratis { get; set; } = 3;

    public bool EsIncluido { get; set; } = true;

    [Range(0, int.MaxValue, ErrorMessage = "El orden no puede ser negativo.")]
    public int Orden { get; set; }

    public bool Activo { get; set; } = true;
}

/// <summary>
/// Representación de salida de un grupo de toppings.
/// </summary>
/// <remarks>
/// <see cref="MaxExtrasGratis"/> es el límite de gratuidad para los Extras de ESTE grupo
/// específico. No confundir con <c>Categoria.MaxAderezosGratis</c>, que es la gratuidad de
/// Aderezos a nivel de la categoría completa.
/// </remarks>
public sealed record ToppingGrupoResponse(
    string Id,
    string Nombre,
    int MaxExtrasGratis,
    bool EsIncluido,
    int Orden,
    bool Activo);
