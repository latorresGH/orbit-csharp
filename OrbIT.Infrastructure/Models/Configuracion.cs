using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Configuracion
{
    public string Id { get; set; } = null!;

    public string Clave { get; set; } = null!;

    public string Valor { get; set; } = null!;

    public string? Descripcion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
