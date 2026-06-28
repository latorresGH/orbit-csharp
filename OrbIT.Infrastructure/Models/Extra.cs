using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Extra
{
    public string Id { get; set; } = null!;

    public UnidadMedida UnidadMedida { get; set; }

    public string Nombre { get; set; } = null!;

    public double Precio { get; set; }

    public double StockActual { get; set; }

    public bool Activo { get; set; }

    public string Categoria { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? InsumoId { get; set; }

    public bool EsGlobal { get; set; }

    public bool EsPremium { get; set; }

    public string? ToppingGrupoId { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<ExtraCategorium> ExtraCategoria { get; set; } = new List<ExtraCategorium>();

    public virtual ICollection<ExtraConsumo> ExtraConsumos { get; set; } = new List<ExtraConsumo>();

    public virtual ICollection<ExtraPrecio> ExtraPrecios { get; set; } = new List<ExtraPrecio>();

    public virtual Insumo? Insumo { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ToppingGrupo? ToppingGrupo { get; set; }
}
