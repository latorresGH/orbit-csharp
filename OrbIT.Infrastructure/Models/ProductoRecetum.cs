using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class ProductoRecetum
{
    public string Id { get; set; } = null!;

    public string ProductoId { get; set; } = null!;

    public string InsumoId { get; set; } = null!;

    public double Cantidad { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Insumo Insumo { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Producto Producto { get; set; } = null!;
}
