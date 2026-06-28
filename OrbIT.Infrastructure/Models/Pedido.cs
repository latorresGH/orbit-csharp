using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Pedido
{
    public string Id { get; set; } = null!;

    public TipoPedido Tipo { get; set; }

    public EstadoPedido Estado { get; set; }

    public EstadoPago EstadoPago { get; set; }

    public MetodoPago? MetodoPago { get; set; }

    public Role? CanceladoPor { get; set; }

    public double Total { get; set; }

    public string? Direccion { get; set; }

    public string? MotivoCancelacion { get; set; }

    public string? RepartidorId { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? NombreCliente { get; set; }

    public string? NumeroCliente { get; set; }

    public string? ApellidoCliente { get; set; }

    public double CostoEnvio { get; set; }

    public string? Departamento { get; set; }

    public string? DireccionFormateada { get; set; }

    public double? DireccionLat { get; set; }

    public double? DireccionLng { get; set; }

    public string? NotasRepartidor { get; set; }

    public string? Piso { get; set; }

    public string? Referencias { get; set; }

    public string? ShippingReason { get; set; }

    public string? ShippingZoneName { get; set; }

    public string? DireccionPrecision { get; set; }

    public bool CuentaAbierta { get; set; }

    public string? ClienteId { get; set; }

    public string? MesaId { get; set; }

    public int? DemoraEstimadaMin { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<CajaMovimiento> CajaMovimientos { get; set; } = new List<CajaMovimiento>();

    public virtual Cliente? Cliente { get; set; }

    public virtual Mesa? Mesa { get; set; }

    public virtual Mesa? MesaNavigation { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<PedidoDetalle> PedidoDetalles { get; set; } = new List<PedidoDetalle>();

    public virtual ICollection<PedidoOfertum> PedidoOferta { get; set; } = new List<PedidoOfertum>();

    public virtual User? Repartidor { get; set; }
}
