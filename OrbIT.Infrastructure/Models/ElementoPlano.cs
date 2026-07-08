using System.Text.Json.Serialization;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Un elemento del plano de salón (mesa, pared o barra), serializado como item del array jsonb
/// <c>elementos</c> de <see cref="PlanoSalon"/>. NO es una entidad: no tiene tabla propia ni change-tracking
/// individual; vive embebido en el JSON de su plano.
///
/// Los nombres de propiedad se fijan con <see cref="JsonPropertyNameAttribute"/> (camelCase) para que el
/// contrato serializado sea estable e independiente de las <c>JsonSerializerOptions</c>: el mismo shape se usa
/// tanto al persistir en la columna jsonb (con las opciones por defecto del ValueConverter) como en el body
/// HTTP (camelCase global de la Api).
/// </summary>
public sealed class ElementoPlano
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    /// <summary>Tipo del elemento: <c>"mesa"</c> | <c>"pared"</c> | <c>"barra"</c>.</summary>
    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = null!;

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Forma del elemento: <c>"cuadrada"</c> | <c>"rectangular"</c> | <c>"recortada"</c>.</summary>
    [JsonPropertyName("forma")]
    public string Forma { get; set; } = null!;

    /// <summary>Id de la <c>Mesa</c> real vinculada (sólo tipo <c>"mesa"</c>); null si no está vinculada.</summary>
    [JsonPropertyName("mesaId")]
    public string? MesaId { get; set; }

    /// <summary>Etiqueta de texto (ej. "Barra"); null si no aplica.</summary>
    [JsonPropertyName("etiqueta")]
    public string? Etiqueta { get; set; }

    /// <summary>Capacidad en personas (sólo tipo <c>"mesa"</c>); null si no aplica.</summary>
    [JsonPropertyName("capacidad")]
    public int? Capacidad { get; set; }

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }
}
