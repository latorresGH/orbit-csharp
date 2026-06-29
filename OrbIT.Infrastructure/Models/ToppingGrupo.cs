using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class ToppingGrupo
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public int MaxExtrasGratis { get; set; }

    public bool EsIncluido { get; set; }

    public int Orden { get; set; }

    public bool Activo { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<Extra> Extras { get; set; } = new List<Extra>();

    public virtual Negocio Negocio { get; set; } = null!;
}
