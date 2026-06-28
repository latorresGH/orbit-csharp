using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class GastoOperativo
{
    public string Id { get; set; } = null!;

    public string Categoria { get; set; } = null!;

    public double Monto { get; set; }

    public string? Descripcion { get; set; }

    public DateTime Fecha { get; set; }

    public string? ComprobanteUrl { get; set; }

    public string? CreadoPor { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
