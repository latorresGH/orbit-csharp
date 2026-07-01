using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Negocio
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public bool Activo { get; set; }

    public string Plan { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? MpCustomerId { get; set; }

    public string? MpSuscripcionId { get; set; }

    public DateTime? TrialExpira { get; set; }

    public DateTime? CuentaCerradaAt { get; set; }

    public string? LogoUrl { get; set; }

    // FK al Plan de suscripción (tabla global). Nullable: un negocio puede no tener plan asignado (ON DELETE
    // SET NULL). La nav se llama PlanNavigation para no colisionar con la propiedad legacy `Plan` (string),
    // misma convención de scaffold que MesaNavigation en Pedido.
    public string? PlanId { get; set; }

    public virtual Plan? PlanNavigation { get; set; }

    public virtual ICollection<AderezoCategorium> AderezoCategoria { get; set; } = new List<AderezoCategorium>();

    public virtual ICollection<AderezoConsumo> AderezoConsumos { get; set; } = new List<AderezoConsumo>();

    public virtual ICollection<AderezoPrecio> AderezoPrecios { get; set; } = new List<AderezoPrecio>();

    public virtual ICollection<Aderezo> Aderezos { get; set; } = new List<Aderezo>();

    public virtual ICollection<Barrio> Barrios { get; set; } = new List<Barrio>();

    public virtual ICollection<CajaMovimiento> CajaMovimientos { get; set; } = new List<CajaMovimiento>();

    public virtual ICollection<Categorium> Categoria { get; set; } = new List<Categorium>();

    public virtual ICollection<Cliente> Clientes { get; set; } = new List<Cliente>();

    public virtual ICollection<CodigoDescuento> CodigoDescuentos { get; set; } = new List<CodigoDescuento>();

    public virtual ICollection<Configuracion> Configuracions { get; set; } = new List<Configuracion>();

    public virtual DemoraConfig? DemoraConfig { get; set; }

    public virtual ICollection<ExtraCategorium> ExtraCategoria { get; set; } = new List<ExtraCategorium>();

    public virtual ICollection<ExtraConsumo> ExtraConsumos { get; set; } = new List<ExtraConsumo>();

    public virtual ICollection<ExtraPrecio> ExtraPrecios { get; set; } = new List<ExtraPrecio>();

    public virtual ICollection<Extra> Extras { get; set; } = new List<Extra>();

    public virtual ICollection<GastoOperativo> GastoOperativos { get; set; } = new List<GastoOperativo>();

    public virtual ICollection<GrupoCombo> GrupoCombos { get; set; } = new List<GrupoCombo>();

    public virtual ICollection<GrupoOpcion> GrupoOpcions { get; set; } = new List<GrupoOpcion>();

    public virtual ICollection<Insumo> Insumos { get; set; } = new List<Insumo>();

    public virtual ICollection<Mesa> Mesas { get; set; } = new List<Mesa>();

    public virtual ICollection<Ofertum> Oferta { get; set; } = new List<Ofertum>();

    public virtual ICollection<OfertaProducto> OfertaProductos { get; set; } = new List<OfertaProducto>();

    public virtual ICollection<PedidoDetalle> PedidoDetalles { get; set; } = new List<PedidoDetalle>();

    public virtual ICollection<PedidoOfertum> PedidoOferta { get; set; } = new List<PedidoOfertum>();

    public virtual ICollection<Pedido> Pedidos { get; set; } = new List<Pedido>();

    public virtual ICollection<PizzaMediaMedium> PizzaMediaMedia { get; set; } = new List<PizzaMediaMedium>();

    public virtual ICollection<ProductoRecetum> ProductoReceta { get; set; } = new List<ProductoRecetum>();

    public virtual ICollection<Producto> Productos { get; set; } = new List<Producto>();

    public virtual ICollection<Proveedor> Proveedors { get; set; } = new List<Proveedor>();

    public virtual ICollection<StockMovimiento> StockMovimientos { get; set; } = new List<StockMovimiento>();

    public virtual ICollection<ToppingGrupo> ToppingGrupos { get; set; } = new List<ToppingGrupo>();

    public virtual ICollection<Turno> Turnos { get; set; } = new List<Turno>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
