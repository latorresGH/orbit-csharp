using System.ComponentModel.DataAnnotations;
using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Productos;

// ─────────────────────────────────────────────────────────────────────────────
// Requests
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Body para crear un producto con su receta. El <c>NegocioId</c> NO viaja en el body: se estampa
/// desde el tenant activo. La <see cref="CategoriaId"/> es obligatoria y se valida contra el tenant
/// (categoría inexistente o de otro negocio → 400 'Categoría inválida', paridad NestJS). El producto
/// se crea siempre <c>activo=true</c>.
/// </summary>
public sealed class CreateProductoRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string Nombre { get; set; } = null!;

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double Precio { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "La categoría es obligatoria.")]
    public string CategoriaId { get; set; } = null!;

    public string? Descripcion { get; set; }

    public string? ImagenUrl { get; set; }

    public string? Codigo { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "El tiempo de preparación no puede ser negativo.")]
    public int? TiempoPreparacionMin { get; set; }

    public bool EsVegetariano { get; set; }

    public string? Badge { get; set; }

    public bool PermitirMediaMedia { get; set; }

    public bool AceptaSalsas { get; set; } = true;

    public bool AceptaToppings { get; set; } = true;

    /// <summary>IDs de grupos de toppings compatibles. Se persiste como jsonb (List&lt;string&gt; tipada).</summary>
    public List<string>? ToppingGruposCompatibles { get; set; }

    /// <summary>Receta (insumos consumidos). Puede venir vacía. Cada insumoId se valida contra el tenant.</summary>
    public List<RecetaItemInput>? Receta { get; set; }
}

/// <summary>
/// Body para actualizar un producto (semántica PATCH parcial: sólo se tocan los campos presentes).
/// Convención del proyecto (igual a <c>UpdateExtraRequest</c>): <c>null</c> = no tocar. Para
/// <see cref="CategoriaId"/> y los strings borrables (<see cref="Codigo"/>, <see cref="ImagenUrl"/>,
/// <see cref="Badge"/>): cadena vacía = limpiar. <see cref="Receta"/> presente (aunque vacía) reemplaza
/// por completo la receta; ausente la deja intacta. <see cref="ToppingGruposCompatibles"/> igual.
/// </summary>
public sealed class UpdateProductoRequest
{
    [StringLength(120, MinimumLength = 1, ErrorMessage = "El nombre debe tener entre 1 y 120 caracteres.")]
    public string? Nombre { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "El precio no puede ser negativo.")]
    public double? Precio { get; set; }

    /// <summary><c>null</c> = no tocar; cadena vacía = desvincular categoría; valor = vincular (validado).</summary>
    public string? CategoriaId { get; set; }

    public string? Descripcion { get; set; }

    public string? ImagenUrl { get; set; }

    public string? Codigo { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "El tiempo de preparación no puede ser negativo.")]
    public int? TiempoPreparacionMin { get; set; }

    public bool? EsVegetariano { get; set; }

    public string? Badge { get; set; }

    public bool? PermitirMediaMedia { get; set; }

    public bool? AceptaSalsas { get; set; }

    public bool? AceptaToppings { get; set; }

    public List<string>? ToppingGruposCompatibles { get; set; }

    public List<RecetaItemInput>? Receta { get; set; }
}

/// <summary>Ítem de receta entrante: insumo + cantidad consumida por unidad de producto.</summary>
public sealed class RecetaItemInput
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "El insumo es obligatorio.")]
    public string InsumoId { get; set; } = null!;

    [Range(0.0001, double.MaxValue, ErrorMessage = "La cantidad debe ser mayor a cero.")]
    public double Cantidad { get; set; }
}

/// <summary>Body para activar/desactivar un producto (endpoint <c>/activo</c>).</summary>
public sealed class ToggleProductoActivoRequest
{
    public bool Activo { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Responses
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Categoría embebida (id + nombre) en las respuestas de producto.</summary>
public sealed record CategoriaRefResponse(string Id, string Nombre);

/// <summary>
/// Producto en su versión "básica" para el menú público (<c>?basico=true</c>): SIN receta, sólo el
/// booleano <see cref="TieneReceta"/> (EXISTS correlacionado, no se cargan filas de receta).
/// </summary>
public sealed record ProductoBasicoResponse(
    string Id,
    string Nombre,
    double Precio,
    bool Activo,
    string? Badge,
    bool EsVegetariano,
    string? ImagenUrl,
    int? TiempoPreparacionMin,
    bool PermitirMediaMedia,
    bool AceptaSalsas,
    bool AceptaToppings,
    List<string> ToppingGruposCompatibles,
    string? CategoriaId,
    CategoriaRefResponse? Categoria,
    bool TieneReceta);

/// <summary>Ítem de receta de salida: insumo (con su unidad) + cantidad.</summary>
public sealed record RecetaItemResponse(string InsumoId, double Cantidad, InsumoRefResponse Insumo);

/// <summary>Insumo embebido en un ítem de receta.</summary>
public sealed record InsumoRefResponse(string Id, string Nombre, UnidadMedida UnidadMedida);

/// <summary>
/// Producto completo (menú admin con JWT y detalle por id): incluye la <see cref="Receta"/> con sus
/// insumos. Se usa tanto para el listado completo como para el GET por id.
/// </summary>
public sealed record ProductoResponse(
    string Id,
    string Nombre,
    double Precio,
    bool Activo,
    bool EsParaVenta,
    string? Descripcion,
    string? Codigo,
    string? Badge,
    bool EsVegetariano,
    string? ImagenUrl,
    int? TiempoPreparacionMin,
    bool PermitirMediaMedia,
    bool AceptaSalsas,
    bool AceptaToppings,
    List<string> ToppingGruposCompatibles,
    string? CategoriaId,
    CategoriaRefResponse? Categoria,
    List<RecetaItemResponse> Receta);
