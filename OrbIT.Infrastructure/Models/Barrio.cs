using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Barrio
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public double PrecioEnvio { get; set; }

    public bool Activo { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
