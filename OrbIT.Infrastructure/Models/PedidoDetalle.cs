using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class PedidoDetalle
{
    public string Id { get; set; } = null!;

    public string PedidoId { get; set; } = null!;

    public string ProductoId { get; set; } = null!;

    public int Cantidad { get; set; }

    public double Subtotal { get; set; }

    public string? Notas { get; set; }

    public string? Extras { get; set; }

    public double PrecioUnitario { get; set; }

    public bool SinExtras { get; set; }

    public bool ImpresoEnCocina { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Pedido Pedido { get; set; } = null!;

    public virtual PedidoOfertum? PedidoOfertum { get; set; }

    public virtual PizzaMediaMedium? PizzaMediaMedium { get; set; }

    public virtual Producto Producto { get; set; } = null!;

    public virtual ICollection<Aderezo> As { get; set; } = new List<Aderezo>();
}
