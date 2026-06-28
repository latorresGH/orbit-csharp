using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class StockMovimiento
{
    public string Id { get; set; } = null!;

    public string? InsumoId { get; set; }

    public string Tipo { get; set; } = null!;

    public double Cantidad { get; set; }

    public double StockAntes { get; set; }

    public double StockDespues { get; set; }

    public string? PedidoId { get; set; }

    public string? Motivo { get; set; }

    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? AderezoId { get; set; }

    public string? ExtraId { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Insumo? Insumo { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;
}
