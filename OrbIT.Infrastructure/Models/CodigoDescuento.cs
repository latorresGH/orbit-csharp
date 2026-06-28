using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class CodigoDescuento
{
    public string Id { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public string Codigo { get; set; } = null!;

    public string? Descripcion { get; set; }

    public string TipoDescuento { get; set; } = null!;

    public double Valor { get; set; }

    public string? ProductoId { get; set; }

    public DateTime FechaInicio { get; set; }

    public DateTime FechaFin { get; set; }

    public bool Activo { get; set; }

    public int? UsosMaximos { get; set; }

    public int UsosActuales { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Producto? Producto { get; set; }
}
