using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class DemoraConfig
{
    public string Id { get; set; } = null!;

    public string Modo { get; set; } = null!;

    public int ValorManual { get; set; }

    public string? Rangos { get; set; }

    public bool Activo { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
