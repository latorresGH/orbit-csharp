using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Categorias;

/// <summary>
/// Body para crear una categoría. La unicidad del nombre es por negocio (tenant);
/// el <c>NegocioId</c> NO viaja en el body: se estampa en el servidor a partir del
/// tenant activo del usuario autenticado.
/// </summary>
public sealed class CreateCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    [StringLength(500, ErrorMessage = "La descripción no puede superar los 500 caracteres.")]
    public string? Descripcion { get; set; }

    public bool Activo { get; set; } = true;

    [Range(0, int.MaxValue, ErrorMessage = "El orden no puede ser negativo.")]
    public int Orden { get; set; }

    /// <summary>
    /// Cantidad de aderezos que el cliente puede elegir sin costo en esta categoría.
    /// IMPORTANTE: este límite aplica SOLO a Aderezos. La gratuidad de Extras (toppings)
    /// vive en <c>ToppingGrupo.MaxExtrasGratis</c> y se gestiona en su propio controller.
    /// Por defecto 2 (mismo default que la columna en la base).
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "El máximo de aderezos gratis no puede ser negativo.")]
    public int MaxAderezosGratis { get; set; } = 2;
}

/// <summary>
/// Body para actualizar una categoría existente (reemplazo total de los campos editables).
/// Igual que en el create, la unicidad del nombre es por negocio.
/// </summary>
public sealed class UpdateCategoriaRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 100 caracteres.")]
    public string Nombre { get; set; } = null!;

    [StringLength(500, ErrorMessage = "La descripción no puede superar los 500 caracteres.")]
    public string? Descripcion { get; set; }

    public bool Activo { get; set; } = true;

    [Range(0, int.MaxValue, ErrorMessage = "El orden no puede ser negativo.")]
    public int Orden { get; set; }

    /// <summary>
    /// Máximo de aderezos gratis. SOLO aplica a Aderezos (ver <see cref="CreateCategoriaRequest.MaxAderezosGratis"/>).
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "El máximo de aderezos gratis no puede ser negativo.")]
    public int MaxAderezosGratis { get; set; } = 2;
}

/// <summary>
/// Body para reordenar categorías (D4): la lista de ids define el nuevo orden y el
/// índice de cada id pasa a ser su campo <c>Orden</c> (0-based).
/// </summary>
public sealed class ReorderCategoriasRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "Se requiere al menos un id.")]
    public List<string> IdsEnOrden { get; set; } = new();
}

/// <summary>
/// Representación de salida de una categoría.
/// </summary>
/// <remarks>
/// <see cref="MaxAderezosGratis"/> es la cantidad de aderezos gratis de la categoría y
/// aplica EXCLUSIVAMENTE a Aderezos. No confundir con la gratuidad de Extras/toppings,
/// que se modela aparte en <c>ToppingGrupo.MaxExtrasGratis</c>.
/// </remarks>
public sealed record CategoriaResponse(
    string Id,
    string Nombre,
    string? Descripcion,
    bool Activo,
    int Orden,
    int MaxAderezosGratis,
    DateTime CreatedAt,
    DateTime UpdatedAt);
