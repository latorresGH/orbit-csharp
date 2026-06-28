using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class PedidoOfertum
{
    public string Id { get; set; } = null!;

    public string PedidoId { get; set; } = null!;

    public string OfertaId { get; set; } = null!;

    public string? PedidoDetalleId { get; set; }

    public double PrecioOriginal { get; set; }

    public double PrecioFinal { get; set; }

    public double DescuentoAplicado { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Ofertum Oferta { get; set; } = null!;

    public virtual Pedido Pedido { get; set; } = null!;

    public virtual PedidoDetalle? PedidoDetalle { get; set; }
}
