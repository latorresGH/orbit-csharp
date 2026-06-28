using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class ExtraCategorium
{
    public string Id { get; set; } = null!;

    public string ExtraId { get; set; } = null!;

    public string CategoriaId { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public virtual Categorium Categoria { get; set; } = null!;

    public virtual Extra Extra { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
