using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class GrupoCombo
{
    public string Id { get; set; } = null!;

    public string OfertaId { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public bool Obligatorio { get; set; }

    public int Cantidad { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<GrupoOpcion> GrupoOpcions { get; set; } = new List<GrupoOpcion>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Ofertum Oferta { get; set; } = null!;
}
