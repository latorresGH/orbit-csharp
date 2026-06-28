using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class PizzaMediaMedium
{
    public string Id { get; set; } = null!;

    public string PedidoDetalleId { get; set; } = null!;

    public string Sabor1Id { get; set; } = null!;

    public string Sabor2Id { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual PedidoDetalle PedidoDetalle { get; set; } = null!;
}
