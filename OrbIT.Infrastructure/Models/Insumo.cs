using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Insumo
{
    public string Id { get; set; } = null!;

    public UnidadMedida UnidadMedida { get; set; }

    public string Nombre { get; set; } = null!;

    public double StockActual { get; set; }

    public double StockMinimo { get; set; }

    public bool Activo { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? ProveedorId { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<Extra> Extras { get; set; } = new List<Extra>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<ProductoRecetum> ProductoReceta { get; set; } = new List<ProductoRecetum>();

    public virtual Proveedor? Proveedor { get; set; }

    public virtual ICollection<StockMovimiento> StockMovimientos { get; set; } = new List<StockMovimiento>();
}
