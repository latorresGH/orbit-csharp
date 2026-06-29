using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Categorium
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public string? Descripcion { get; set; }

    public bool Activo { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int Orden { get; set; }

    public int MaxAderezosGratis { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<AderezoCategorium> AderezoCategoria { get; set; } = new List<AderezoCategorium>();

    public virtual ICollection<AderezoConsumo> AderezoConsumos { get; set; } = new List<AderezoConsumo>();

    public virtual ICollection<AderezoPrecio> AderezoPrecios { get; set; } = new List<AderezoPrecio>();

    public virtual ICollection<ExtraCategorium> ExtraCategoria { get; set; } = new List<ExtraCategorium>();

    public virtual ICollection<ExtraConsumo> ExtraConsumos { get; set; } = new List<ExtraConsumo>();

    public virtual ICollection<ExtraPrecio> ExtraPrecios { get; set; } = new List<ExtraPrecio>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<Producto> Productos { get; set; } = new List<Producto>();
}
