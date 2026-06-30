using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Producto
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public double Precio { get; set; }

    public bool EsParaVenta { get; set; }

    public bool Activo { get; set; }

    public string? CategoriaId { get; set; }

    public string? Codigo { get; set; }

    public string? Descripcion { get; set; }

    public int? TiempoPreparacionMin { get; set; }

    public string? ImagenUrl { get; set; }

    public string? Badge { get; set; }

    public bool EsVegetariano { get; set; }

    // Tweak post-scaffold (decisión A): la columna jsonb `toppingGruposCompatibles` se mapea a una
    // List<string> tipada vía ValueConverter + ValueComparer configurados en OnModelCreatingPartial
    // (OrbitDbContext.MultiTenant.cs), en vez del string crudo que emite el scaffold. Si se vuelve a
    // scaffoldear, reaplicar este cambio de tipo.
    public List<string> ToppingGruposCompatibles { get; set; } = new();

    public bool PermitirMediaMedia { get; set; }

    public bool AceptaSalsas { get; set; }

    public bool AceptaToppings { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual Categorium? Categoria { get; set; }

    public virtual ICollection<CodigoDescuento> CodigoDescuentos { get; set; } = new List<CodigoDescuento>();

    public virtual ICollection<GrupoOpcion> GrupoOpcions { get; set; } = new List<GrupoOpcion>();

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual ICollection<OfertaProducto> OfertaProductos { get; set; } = new List<OfertaProducto>();

    public virtual ICollection<PedidoDetalle> PedidoDetalles { get; set; } = new List<PedidoDetalle>();

    public virtual ICollection<ProductoRecetum> ProductoReceta { get; set; } = new List<ProductoRecetum>();
}
