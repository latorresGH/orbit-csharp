using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Mesa
{
    public string Id { get; set; } = null!;

    public EstadoMesa Estado { get; set; }

    public int Numero { get; set; }

    public string? Nombre { get; set; }

    public int Capacidad { get; set; }

    public bool Activa { get; set; }

    public int PosX { get; set; }

    public int PosY { get; set; }

    public string? PedidoActivoId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Pedido? PedidoActivo { get; set; }

    public virtual ICollection<Pedido> Pedidos { get; set; } = new List<Pedido>();
}
