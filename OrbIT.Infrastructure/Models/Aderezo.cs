using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Aderezo
{
    public string Id { get; set; } = null!;

    public UnidadMedida UnidadMedida { get; set; }

    public string Nombre { get; set; } = null!;

    public bool Activo { get; set; }

    public double StockActual { get; set; }

    public bool EsGlobal { get; set; }

    public bool EsPremium { get; set; }

    public double Precio { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<AderezoCategorium> AderezoCategoria { get; set; } = new List<AderezoCategorium>();

    public virtual ICollection<AderezoConsumo> AderezoConsumos { get; set; } = new List<AderezoConsumo>();

    public virtual ICollection<AderezoPrecio> AderezoPrecios { get; set; } = new List<AderezoPrecio>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<PedidoDetalle> Bs { get; set; } = new List<PedidoDetalle>();
}
