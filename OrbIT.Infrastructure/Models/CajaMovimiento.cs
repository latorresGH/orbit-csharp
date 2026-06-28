using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class CajaMovimiento
{
    public string Id { get; set; } = null!;

    public TipoMovimientoCaja Tipo { get; set; }

    public string? PedidoId { get; set; }

    public double MontoTotal { get; set; }

    public double GananciaNegocio { get; set; }

    public double GananciaRepartidor { get; set; }

    public string? Descripcion { get; set; }

    public string? ConfirmadoPor { get; set; }

    public DateTime? FechaConfirmacion { get; set; }

    public DateTime CreatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public bool Anulado { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Pedido? Pedido { get; set; }
}
