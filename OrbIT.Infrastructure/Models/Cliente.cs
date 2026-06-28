using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Cliente
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public string? Apellido { get; set; }

    public string Telefono { get; set; } = null!;

    public string? DireccionFavorita { get; set; }

    public int TotalPedidos { get; set; }

    public double TotalGastado { get; set; }

    public DateTime? FechaUltimoPedido { get; set; }

    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<Pedido> Pedidos { get; set; } = new List<Pedido>();
}
