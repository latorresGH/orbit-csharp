using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Ofertum
{
    public string Id { get; set; } = null!;

    public TipoOferta Tipo { get; set; }

    public EstadoOferta Estado { get; set; }

    public string Nombre { get; set; } = null!;

    public string? Descripcion { get; set; }

    public DateTime FechaInicio { get; set; }

    public DateTime? FechaFin { get; set; }

    public bool Activa { get; set; }

    public double? PorcentajeDescuento { get; set; }

    public double? MontoDescuento { get; set; }

    public int? MaxUsosPorCliente { get; set; }

    public int? MaxUsosTotales { get; set; }

    public int UsosActuales { get; set; }

    public string DiasAplicables { get; set; } = null!;

    public string? HoraInicio { get; set; }

    public string? HoraFin { get; set; }

    public bool AplicaPorLinea { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<GrupoCombo> GrupoCombos { get; set; } = new List<GrupoCombo>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<OfertaProducto> OfertaProductos { get; set; } = new List<OfertaProducto>();

    public virtual ICollection<PedidoOfertum> PedidoOferta { get; set; } = new List<PedidoOfertum>();
}
