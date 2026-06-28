using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class OfertaProducto
{
    public string Id { get; set; } = null!;

    public string OfertaId { get; set; } = null!;

    public string ProductoId { get; set; } = null!;

    public bool Obligatorio { get; set; }

    public int CantidadMin { get; set; }

    public int? CantidadMax { get; set; }

    public double? PrecioEspecial { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Ofertum Oferta { get; set; } = null!;

    public virtual Producto Producto { get; set; } = null!;
}
