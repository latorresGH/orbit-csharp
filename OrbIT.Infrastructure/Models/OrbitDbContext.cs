using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class OrbitDbContext : DbContext
{
    public OrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Aderezo> Aderezos { get; set; }

    public virtual DbSet<AderezoCategorium> AderezoCategoria { get; set; }

    public virtual DbSet<AderezoConsumo> AderezoConsumos { get; set; }

    public virtual DbSet<AderezoPrecio> AderezoPrecios { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<Barrio> Barrios { get; set; }

    public virtual DbSet<CajaMovimiento> CajaMovimientos { get; set; }

    public virtual DbSet<Categorium> Categoria { get; set; }

    public virtual DbSet<Cliente> Clientes { get; set; }

    public virtual DbSet<CodigoDescuento> CodigoDescuentos { get; set; }

    public virtual DbSet<Configuracion> Configuracions { get; set; }

    public virtual DbSet<DemoraConfig> DemoraConfigs { get; set; }

    public virtual DbSet<Extra> Extras { get; set; }

    public virtual DbSet<ExtraCategorium> ExtraCategoria { get; set; }

    public virtual DbSet<ExtraConsumo> ExtraConsumos { get; set; }

    public virtual DbSet<ExtraPrecio> ExtraPrecios { get; set; }

    public virtual DbSet<GastoOperativo> GastoOperativos { get; set; }

    public virtual DbSet<GrupoCombo> GrupoCombos { get; set; }

    public virtual DbSet<GrupoOpcion> GrupoOpcions { get; set; }

    public virtual DbSet<Insumo> Insumos { get; set; }

    public virtual DbSet<Mesa> Mesas { get; set; }

    public virtual DbSet<Negocio> Negocios { get; set; }

    public virtual DbSet<OfertaProducto> OfertaProductos { get; set; }

    public virtual DbSet<Ofertum> Oferta { get; set; }

    public virtual DbSet<Pedido> Pedidos { get; set; }

    public virtual DbSet<PedidoDetalle> PedidoDetalles { get; set; }

    public virtual DbSet<PedidoOfertum> PedidoOferta { get; set; }

    public virtual DbSet<PizzaMediaMedium> PizzaMediaMedia { get; set; }

    public virtual DbSet<Plan> Plans { get; set; }

    public virtual DbSet<PlanoSalon> PlanoSalons { get; set; }

    public virtual DbSet<PrismaMigration> PrismaMigrations { get; set; }

    public virtual DbSet<Producto> Productos { get; set; }

    public virtual DbSet<ProductoRecetum> ProductoReceta { get; set; }

    public virtual DbSet<Proveedor> Proveedors { get; set; }

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    public virtual DbSet<StockMovimiento> StockMovimientos { get; set; }

    public virtual DbSet<ToppingGrupo> ToppingGrupos { get; set; }

    public virtual DbSet<TempToken> TempTokens { get; set; }

    public virtual DbSet<Turno> Turnos { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<WebhookEvent> WebhookEvents { get; set; }

    // Serialización de la columna jsonb `elementos` de PlanoSalon (List<ElementoPlano> <-> texto jsonb). El
    // ElementoPlano fija sus claves con [JsonPropertyName] (camelCase), así que las opciones por defecto ya
    // producen el shape esperado. El ValueComparer es obligatorio para tipos de referencia mutables: sin él
    // EF no detecta que la lista cambió (mismo objeto, contenido distinto) y el UPDATE se pierde; comparamos
    // y snapshoteamos por serialización JSON (deep, robusto ante reordenamientos de propiedades).
    private static readonly ValueConverter<List<ElementoPlano>, string> ElementosConverter = new(
        v => JsonSerializer.Serialize(v ?? new List<ElementoPlano>(), (JsonSerializerOptions?)null),
        v => string.IsNullOrEmpty(v)
            ? new List<ElementoPlano>()
            : JsonSerializer.Deserialize<List<ElementoPlano>>(v, (JsonSerializerOptions?)null) ?? new List<ElementoPlano>());

    private static readonly ValueComparer<List<ElementoPlano>> ElementosComparer = new(
        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
        v => v == null ? 0 : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
        v => JsonSerializer.Deserialize<List<ElementoPlano>>(
            JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new List<ElementoPlano>());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var enumNameTranslator = new ExactNameTranslator();
        modelBuilder
            .HasPostgresEnum<EstadoMesa>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<EstadoOferta>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<EstadoPago>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<EstadoPedido>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<MetodoPago>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<Role>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<TipoMovimientoCaja>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<TipoOferta>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<TipoPedido>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<TipoTurno>(nameTranslator: enumNameTranslator)
            .HasPostgresEnum<UnidadMedida>(nameTranslator: enumNameTranslator);

        modelBuilder.Entity<Aderezo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Aderezo_pkey");

            entity.ToTable("Aderezo");

            entity.HasIndex(e => new { e.Nombre, e.NegocioId }, "Aderezo_nombre_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.EsGlobal).HasColumnName("esGlobal");
            entity.Property(e => e.EsPremium).HasColumnName("esPremium");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Precio).HasColumnName("precio");
            entity.Property(e => e.StockActual).HasColumnName("stockActual");
            entity.Property(e => e.UnidadMedida)
                .HasDefaultValueSql("'UNIDAD'::\"UnidadMedida\"")
                .HasColumnName("unidadMedida");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Aderezos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Aderezo_negocioId_fkey");

            entity.HasMany(d => d.Bs).WithMany(p => p.As)
                .UsingEntity<Dictionary<string, object>>(
                    "AderezoToPedidoDetalle",
                    r => r.HasOne<PedidoDetalle>().WithMany()
                        .HasForeignKey("B")
                        .HasConstraintName("_AderezoToPedidoDetalle_B_fkey"),
                    l => l.HasOne<Aderezo>().WithMany()
                        .HasForeignKey("A")
                        .HasConstraintName("_AderezoToPedidoDetalle_A_fkey"),
                    j =>
                    {
                        j.HasKey("A", "B").HasName("_AderezoToPedidoDetalle_AB_pkey");
                        j.ToTable("_AderezoToPedidoDetalle");
                        j.HasIndex(new[] { "B" }, "_AderezoToPedidoDetalle_B_index");
                    });
        });

        modelBuilder.Entity<AderezoCategorium>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("AderezoCategoria_pkey");

            entity.HasIndex(e => new { e.AderezoId, e.CategoriaId }, "AderezoCategoria_aderezoId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AderezoId).HasColumnName("aderezoId");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");

            entity.HasOne(d => d.Aderezo).WithMany(p => p.AderezoCategoria)
                .HasForeignKey(d => d.AderezoId)
                .HasConstraintName("AderezoCategoria_aderezoId_fkey");

            entity.HasOne(d => d.Categoria).WithMany(p => p.AderezoCategoria)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("AderezoCategoria_categoriaId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.AderezoCategoria)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("AderezoCategoria_negocioId_fkey");
        });

        modelBuilder.Entity<AderezoConsumo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("AderezoConsumo_pkey");

            entity.ToTable("AderezoConsumo");

            entity.HasIndex(e => new { e.AderezoId, e.CategoriaId }, "AderezoConsumo_aderezoId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AderezoId).HasColumnName("aderezoId");
            entity.Property(e => e.CantidadConsumo).HasColumnName("cantidadConsumo");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");

            entity.HasOne(d => d.Aderezo).WithMany(p => p.AderezoConsumos)
                .HasForeignKey(d => d.AderezoId)
                .HasConstraintName("AderezoConsumo_aderezoId_fkey");

            entity.HasOne(d => d.Categoria).WithMany(p => p.AderezoConsumos)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("AderezoConsumo_categoriaId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.AderezoConsumos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("AderezoConsumo_negocioId_fkey");
        });

        modelBuilder.Entity<AderezoPrecio>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("AderezoPrecio_pkey");

            entity.ToTable("AderezoPrecio");

            entity.HasIndex(e => new { e.AderezoId, e.CategoriaId }, "AderezoPrecio_aderezoId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AderezoId).HasColumnName("aderezoId");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Precio).HasColumnName("precio");

            entity.HasOne(d => d.Aderezo).WithMany(p => p.AderezoPrecios)
                .HasForeignKey(d => d.AderezoId)
                .HasConstraintName("AderezoPrecio_aderezoId_fkey");

            entity.HasOne(d => d.Categoria).WithMany(p => p.AderezoPrecios)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("AderezoPrecio_categoriaId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.AderezoPrecios)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("AderezoPrecio_negocioId_fkey");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("AuditLog_pkey");

            entity.ToTable("AuditLog");

            entity.HasIndex(e => new { e.NegocioId, e.CreatedAt }, "AuditLog_negocioId_createdAt_idx");

            entity.HasIndex(e => e.UsuarioId, "AuditLog_usuarioId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Accion).HasColumnName("accion");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Detalle)
                .HasColumnType("jsonb")
                .HasColumnName("detalle");
            entity.Property(e => e.Entidad).HasColumnName("entidad");
            entity.Property(e => e.EntidadId).HasColumnName("entidadId");
            entity.Property(e => e.Ip).HasColumnName("ip");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.UsuarioId).HasColumnName("usuarioId");

            entity.HasOne(d => d.Usuario).WithMany(p => p.AuditLogs)
                .HasForeignKey(d => d.UsuarioId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("AuditLog_usuarioId_fkey");
        });

        modelBuilder.Entity<Barrio>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Barrio_pkey");

            entity.ToTable("Barrio");

            entity.HasIndex(e => new { e.Nombre, e.NegocioId }, "Barrio_nombre_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.PrecioEnvio).HasColumnName("precioEnvio");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Barrios)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Barrio_negocioId_fkey");
        });

        modelBuilder.Entity<CajaMovimiento>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("CajaMovimiento_pkey");

            entity.ToTable("CajaMovimiento");

            entity.HasIndex(e => new { e.NegocioId, e.CreatedAt }, "CajaMovimiento_negocioId_createdAt_idx");

            // Cierre de turno / reportes de caja filtran por FechaConfirmacion (no CreatedAt): índice dedicado.
            entity.HasIndex(e => new { e.NegocioId, e.FechaConfirmacion }, "CajaMovimiento_negocioId_fechaConfirmacion_idx");

            entity.HasIndex(e => e.NegocioId, "CajaMovimiento_negocioId_idx");

            entity.HasIndex(e => e.PedidoId, "CajaMovimiento_pedidoId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Anulado).HasColumnName("anulado");
            entity.Property(e => e.ConfirmadoPor).HasColumnName("confirmadoPor");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.FechaConfirmacion)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaConfirmacion");
            entity.Property(e => e.GananciaNegocio).HasColumnName("gananciaNegocio");
            entity.Property(e => e.GananciaRepartidor).HasColumnName("gananciaRepartidor");
            entity.Property(e => e.MontoTotal).HasColumnName("montoTotal");
            entity.Property(e => e.Tipo).HasColumnName("tipo");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.PedidoId).HasColumnName("pedidoId");

            entity.HasOne(d => d.Negocio).WithMany(p => p.CajaMovimientos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("CajaMovimiento_negocioId_fkey");

            entity.HasOne(d => d.Pedido).WithMany(p => p.CajaMovimientos)
                .HasForeignKey(d => d.PedidoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("CajaMovimiento_pedidoId_fkey");
        });

        modelBuilder.Entity<Categorium>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Categoria_pkey");

            entity.HasIndex(e => new { e.Nombre, e.NegocioId }, "Categoria_nombre_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.MaxAderezosGratis)
                .HasDefaultValue(2)
                .HasColumnName("maxAderezosGratis");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Orden).HasColumnName("orden");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Categoria)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Categoria_negocioId_fkey");
        });

        modelBuilder.Entity<Cliente>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Cliente_pkey");

            entity.ToTable("Cliente");

            entity.HasIndex(e => e.Telefono, "Cliente_telefono_idx");

            entity.HasIndex(e => new { e.Telefono, e.NegocioId }, "Cliente_telefono_negocioId_key").IsUnique();

            entity.HasIndex(e => e.TotalPedidos, "Cliente_totalPedidos_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Apellido).HasColumnName("apellido");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.DireccionFavorita).HasColumnName("direccionFavorita");
            entity.Property(e => e.FechaUltimoPedido)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaUltimoPedido");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Notas).HasColumnName("notas");
            entity.Property(e => e.Telefono).HasColumnName("telefono");
            entity.Property(e => e.TotalGastado).HasColumnName("totalGastado");
            entity.Property(e => e.TotalPedidos).HasColumnName("totalPedidos");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Clientes)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Cliente_negocioId_fkey");
        });

        modelBuilder.Entity<CodigoDescuento>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("CodigoDescuento_pkey");

            entity.ToTable("CodigoDescuento");

            entity.HasIndex(e => e.Activo, "CodigoDescuento_activo_idx");

            entity.HasIndex(e => new { e.Codigo, e.NegocioId }, "CodigoDescuento_codigo_negocioId_key").IsUnique();

            entity.HasIndex(e => new { e.FechaInicio, e.FechaFin }, "CodigoDescuento_fechaInicio_fechaFin_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.Codigo).HasColumnName("codigo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.FechaFin)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaFin");
            entity.Property(e => e.FechaInicio)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaInicio");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.ProductoId).HasColumnName("productoId");
            entity.Property(e => e.TipoDescuento).HasColumnName("tipoDescuento");
            entity.Property(e => e.UsosActuales).HasColumnName("usosActuales");
            entity.Property(e => e.UsosMaximos).HasColumnName("usosMaximos");
            entity.Property(e => e.Valor).HasColumnName("valor");

            entity.HasOne(d => d.Negocio).WithMany(p => p.CodigoDescuentos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("CodigoDescuento_negocioId_fkey");

            entity.HasOne(d => d.Producto).WithMany(p => p.CodigoDescuentos)
                .HasForeignKey(d => d.ProductoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("CodigoDescuento_productoId_fkey");
        });

        modelBuilder.Entity<Configuracion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Configuracion_pkey");

            entity.ToTable("Configuracion");

            entity.HasIndex(e => new { e.Clave, e.NegocioId }, "Configuracion_clave_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Clave).HasColumnName("clave");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");
            entity.Property(e => e.Valor).HasColumnName("valor");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Configuracions)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Configuracion_negocioId_fkey");
        });

        modelBuilder.Entity<DemoraConfig>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("DemoraConfig_pkey");

            entity.ToTable("DemoraConfig");

            entity.HasIndex(e => e.NegocioId, "DemoraConfig_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo).HasColumnName("activo");
            entity.Property(e => e.Modo)
                .HasDefaultValueSql("'AUTO'::text")
                .HasColumnName("modo");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Rangos)
                .HasColumnType("jsonb")
                .HasColumnName("rangos");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");
            entity.Property(e => e.UpdatedBy).HasColumnName("updatedBy");
            entity.Property(e => e.ValorManual).HasColumnName("valorManual");

            entity.HasOne(d => d.Negocio).WithOne(p => p.DemoraConfig)
                .HasForeignKey<DemoraConfig>(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("DemoraConfig_negocioId_fkey");
        });

        modelBuilder.Entity<Extra>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Extra_pkey");

            entity.ToTable("Extra");

            entity.HasIndex(e => new { e.Nombre, e.NegocioId }, "Extra_nombre_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.Categoria)
                .HasDefaultValueSql("'TOPPINGS'::text")
                .HasColumnName("categoria");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.EsGlobal).HasColumnName("esGlobal");
            entity.Property(e => e.EsPremium).HasColumnName("esPremium");
            entity.Property(e => e.InsumoId).HasColumnName("insumoId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Precio)
                .HasDefaultValue(500.0)
                .HasColumnName("precio");
            entity.Property(e => e.StockActual).HasColumnName("stockActual");
            entity.Property(e => e.ToppingGrupoId).HasColumnName("toppingGrupoId");
            entity.Property(e => e.UnidadMedida)
                .HasDefaultValueSql("'UNIDAD'::\"UnidadMedida\"")
                .HasColumnName("unidadMedida");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Insumo).WithMany(p => p.Extras)
                .HasForeignKey(d => d.InsumoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Extra_insumoId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Extras)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Extra_negocioId_fkey");

            entity.HasOne(d => d.ToppingGrupo).WithMany(p => p.Extras)
                .HasForeignKey(d => d.ToppingGrupoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Extra_toppingGrupoId_fkey");
        });

        modelBuilder.Entity<ExtraCategorium>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ExtraCategoria_pkey");

            entity.HasIndex(e => new { e.ExtraId, e.CategoriaId }, "ExtraCategoria_extraId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.ExtraId).HasColumnName("extraId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");

            entity.HasOne(d => d.Categoria).WithMany(p => p.ExtraCategoria)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("ExtraCategoria_categoriaId_fkey");

            entity.HasOne(d => d.Extra).WithMany(p => p.ExtraCategoria)
                .HasForeignKey(d => d.ExtraId)
                .HasConstraintName("ExtraCategoria_extraId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.ExtraCategoria)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ExtraCategoria_negocioId_fkey");
        });

        modelBuilder.Entity<ExtraConsumo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ExtraConsumo_pkey");

            entity.ToTable("ExtraConsumo");

            entity.HasIndex(e => new { e.ExtraId, e.CategoriaId }, "ExtraConsumo_extraId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CantidadConsumo).HasColumnName("cantidadConsumo");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.ExtraId).HasColumnName("extraId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");

            entity.HasOne(d => d.Categoria).WithMany(p => p.ExtraConsumos)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("ExtraConsumo_categoriaId_fkey");

            entity.HasOne(d => d.Extra).WithMany(p => p.ExtraConsumos)
                .HasForeignKey(d => d.ExtraId)
                .HasConstraintName("ExtraConsumo_extraId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.ExtraConsumos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ExtraConsumo_negocioId_fkey");
        });

        modelBuilder.Entity<ExtraPrecio>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ExtraPrecio_pkey");

            entity.ToTable("ExtraPrecio");

            entity.HasIndex(e => new { e.ExtraId, e.CategoriaId }, "ExtraPrecio_extraId_categoriaId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.ExtraId).HasColumnName("extraId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Precio).HasColumnName("precio");

            entity.HasOne(d => d.Categoria).WithMany(p => p.ExtraPrecios)
                .HasForeignKey(d => d.CategoriaId)
                .HasConstraintName("ExtraPrecio_categoriaId_fkey");

            entity.HasOne(d => d.Extra).WithMany(p => p.ExtraPrecios)
                .HasForeignKey(d => d.ExtraId)
                .HasConstraintName("ExtraPrecio_extraId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.ExtraPrecios)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ExtraPrecio_negocioId_fkey");
        });

        modelBuilder.Entity<GastoOperativo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GastoOperativo_pkey");

            entity.ToTable("GastoOperativo");

            entity.HasIndex(e => e.Categoria, "GastoOperativo_categoria_idx");

            entity.HasIndex(e => e.CreatedAt, "GastoOperativo_createdAt_idx");

            entity.HasIndex(e => e.Fecha, "GastoOperativo_fecha_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Categoria).HasColumnName("categoria");
            entity.Property(e => e.ComprobanteUrl).HasColumnName("comprobanteUrl");
            entity.Property(e => e.CreadoPor).HasColumnName("creadoPor");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.Fecha)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fecha");
            entity.Property(e => e.Monto).HasColumnName("monto");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.GastoOperativos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("GastoOperativo_negocioId_fkey");
        });

        modelBuilder.Entity<GrupoCombo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GrupoCombo_pkey");

            entity.ToTable("GrupoCombo");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Cantidad)
                .HasDefaultValue(1)
                .HasColumnName("cantidad");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Obligatorio)
                .HasDefaultValue(true)
                .HasColumnName("obligatorio");
            entity.Property(e => e.OfertaId).HasColumnName("ofertaId");

            entity.HasOne(d => d.Negocio).WithMany(p => p.GrupoCombos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("GrupoCombo_negocioId_fkey");

            entity.HasOne(d => d.Oferta).WithMany(p => p.GrupoCombos)
                .HasForeignKey(d => d.OfertaId)
                .HasConstraintName("GrupoCombo_ofertaId_fkey");
        });

        modelBuilder.Entity<GrupoOpcion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GrupoOpcion_pkey");

            entity.ToTable("GrupoOpcion");

            entity.HasIndex(e => new { e.GrupoComboId, e.ProductoId }, "GrupoOpcion_grupoComboId_productoId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GrupoComboId).HasColumnName("grupoComboId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.ProductoId).HasColumnName("productoId");

            entity.HasOne(d => d.GrupoCombo).WithMany(p => p.GrupoOpcions)
                .HasForeignKey(d => d.GrupoComboId)
                .HasConstraintName("GrupoOpcion_grupoComboId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.GrupoOpcions)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("GrupoOpcion_negocioId_fkey");

            entity.HasOne(d => d.Producto).WithMany(p => p.GrupoOpcions)
                .HasForeignKey(d => d.ProductoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("GrupoOpcion_productoId_fkey");
        });

        modelBuilder.Entity<Insumo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Insumo_pkey");

            entity.ToTable("Insumo");

            entity.HasIndex(e => new { e.NegocioId, e.Activo }, "Insumo_negocioId_activo_idx");

            entity.HasIndex(e => e.NegocioId, "Insumo_negocioId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.ProveedorId).HasColumnName("proveedorId");
            entity.Property(e => e.StockActual).HasColumnName("stockActual");
            entity.Property(e => e.StockMinimo)
                .HasDefaultValue(5.0)
                .HasColumnName("stockMinimo");
            entity.Property(e => e.UnidadMedida)
                .HasDefaultValueSql("'UNIDAD'::\"UnidadMedida\"")
                .HasColumnName("unidadMedida");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Insumos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Insumo_negocioId_fkey");

            entity.HasOne(d => d.Proveedor).WithMany(p => p.Insumos)
                .HasForeignKey(d => d.ProveedorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Insumo_proveedorId_fkey");
        });

        modelBuilder.Entity<Mesa>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Mesa_pkey");

            entity.ToTable("Mesa");

            entity.HasIndex(e => e.Activa, "Mesa_activa_idx");

            entity.HasIndex(e => new { e.Numero, e.NegocioId }, "Mesa_numero_negocioId_key").IsUnique();

            entity.HasIndex(e => e.PedidoActivoId, "Mesa_pedidoActivoId_key").IsUnique();

            entity.HasIndex(e => e.Estado, "Mesa_estado_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activa)
                .HasDefaultValue(true)
                .HasColumnName("activa");
            entity.Property(e => e.Capacidad)
                .HasDefaultValue(4)
                .HasColumnName("capacidad");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Numero).HasColumnName("numero");
            entity.Property(e => e.PedidoActivoId).HasColumnName("pedidoActivoId");
            entity.Property(e => e.PosX).HasColumnName("posX");
            entity.Property(e => e.PosY).HasColumnName("posY");
            entity.Property(e => e.Estado)
                .HasDefaultValueSql("'LIBRE'::\"EstadoMesa\"")
                .HasColumnName("estado");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Mesas)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Mesa_negocioId_fkey");

            entity.HasOne(d => d.PedidoActivo).WithOne(p => p.Mesa)
                .HasForeignKey<Mesa>(d => d.PedidoActivoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Mesa_pedidoActivoId_fkey");
        });

        modelBuilder.Entity<Negocio>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Negocio_pkey");

            entity.ToTable("Negocio");

            entity.HasIndex(e => e.Slug, "Negocio_slug_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.CuentaCerradaAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("cuentaCerradaAt");
            entity.Property(e => e.LogoUrl).HasColumnName("logoUrl");
            entity.Property(e => e.MpCustomerId).HasColumnName("mpCustomerId");
            entity.Property(e => e.MpSuscripcionId).HasColumnName("mpSuscripcionId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Plan)
                .HasDefaultValueSql("'basic'::text")
                .HasColumnName("plan");
            entity.Property(e => e.PlanId).HasColumnName("planId");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.TrialExpira)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("trialExpira");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasIndex(e => e.PlanId, "Negocio_planId_idx");

            entity.HasOne(d => d.PlanNavigation).WithMany(p => p.Negocios)
                .HasForeignKey(d => d.PlanId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Negocio_planId_fkey");
        });

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Plan_pkey");

            entity.ToTable("Plan");

            entity.HasIndex(e => e.Slug, "Plan_slug_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.LimiteProductos)
                .HasDefaultValue(30)
                .HasColumnName("limiteProductos");
            entity.Property(e => e.LimiteUsuarios)
                .HasDefaultValue(3)
                .HasColumnName("limiteUsuarios");
            entity.Property(e => e.MpPlanId).HasColumnName("mpPlanId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.PrecioMensual).HasColumnName("precioMensual");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.TieneImagenes).HasColumnName("tieneImagenes");
            entity.Property(e => e.TieneInsumos).HasColumnName("tieneInsumos");
            entity.Property(e => e.TieneMesas).HasColumnName("tieneMesas");
            entity.Property(e => e.TieneOfertas).HasColumnName("tieneOfertas");
            entity.Property(e => e.TieneReportes).HasColumnName("tieneReportes");
            entity.Property(e => e.TieneSignalR).HasColumnName("tieneSignalR");
            entity.Property(e => e.TieneToppingGrupos).HasColumnName("tieneToppingGrupos");
            // Plan es una tabla GLOBAL del sistema: NO lleva Global Query Filter (a diferencia de las
            // entidades por-tenant). Se ve completa desde cualquier request. Ver OnModelCreatingPartial.
        });

        modelBuilder.Entity<PlanoSalon>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PlanoSalon_pkey");

            entity.ToTable("PlanoSalon");

            // 1:1 con el negocio (índice único) + índice de lookup por tenant. Ambos replican el DDL creado
            // a mano en orbit_csharp para que EnsureCreated (tests) genere el mismo esquema.
            entity.HasIndex(e => e.NegocioId, "PlanoSalon_negocioId_key").IsUnique();
            entity.HasIndex(e => e.NegocioId, "PlanoSalon_negocioId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Elementos)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(ElementosConverter, ElementosComparer)
                .HasColumnName("elementos");
            entity.Property(e => e.CanvasWidth)
                .HasDefaultValue(1200)
                .HasColumnName("canvasWidth");
            entity.Property(e => e.CanvasHeight)
                .HasDefaultValue(800)
                .HasColumnName("canvasHeight");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("updatedAt");

            // ON DELETE CASCADE: si se borra el negocio, su plano se va con él (paridad con el DDL). WithMany()
            // sin navegación inversa evita agregarle una colección a Negocio; la unicidad la garantiza el índice.
            entity.HasOne(d => d.Negocio).WithMany()
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("PlanoSalon_negocioId_fkey");
        });

        modelBuilder.Entity<OfertaProducto>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("OfertaProducto_pkey");

            entity.ToTable("OfertaProducto");

            entity.HasIndex(e => new { e.OfertaId, e.ProductoId }, "OfertaProducto_ofertaId_productoId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CantidadMax).HasColumnName("cantidadMax");
            entity.Property(e => e.CantidadMin)
                .HasDefaultValue(1)
                .HasColumnName("cantidadMin");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Obligatorio).HasColumnName("obligatorio");
            entity.Property(e => e.OfertaId).HasColumnName("ofertaId");
            entity.Property(e => e.PrecioEspecial).HasColumnName("precioEspecial");
            entity.Property(e => e.ProductoId).HasColumnName("productoId");

            entity.HasOne(d => d.Negocio).WithMany(p => p.OfertaProductos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OfertaProducto_negocioId_fkey");

            entity.HasOne(d => d.Oferta).WithMany(p => p.OfertaProductos)
                .HasForeignKey(d => d.OfertaId)
                .HasConstraintName("OfertaProducto_ofertaId_fkey");

            entity.HasOne(d => d.Producto).WithMany(p => p.OfertaProductos)
                .HasForeignKey(d => d.ProductoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OfertaProducto_productoId_fkey");
        });

        modelBuilder.Entity<Ofertum>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Oferta_pkey");

            entity.HasIndex(e => new { e.FechaInicio, e.FechaFin }, "Oferta_fechaInicio_fechaFin_idx");

            entity.HasIndex(e => new { e.Activa, e.Estado }, "Oferta_activa_estado_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activa)
                .HasDefaultValue(true)
                .HasColumnName("activa");
            entity.Property(e => e.AplicaPorLinea)
                .HasDefaultValue(true)
                .HasColumnName("aplicaPorLinea");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.DiasAplicables)
                .HasDefaultValueSql("'1,2,3,4,5,6,7'::text")
                .HasColumnName("diasAplicables");
            entity.Property(e => e.FechaFin)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaFin");
            entity.Property(e => e.FechaInicio)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("fechaInicio");
            entity.Property(e => e.HoraFin).HasColumnName("horaFin");
            entity.Property(e => e.HoraInicio).HasColumnName("horaInicio");
            entity.Property(e => e.MaxUsosPorCliente).HasColumnName("maxUsosPorCliente");
            entity.Property(e => e.MaxUsosTotales).HasColumnName("maxUsosTotales");
            entity.Property(e => e.MontoDescuento).HasColumnName("montoDescuento");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.PorcentajeDescuento).HasColumnName("porcentajeDescuento");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");
            entity.Property(e => e.UsosActuales).HasColumnName("usosActuales");
            entity.Property(e => e.Tipo).HasColumnName("tipo");
            entity.Property(e => e.Estado)
                .HasDefaultValueSql("'ACTIVA'::\"EstadoOferta\"")
                .HasColumnName("estado");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Oferta)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Oferta_negocioId_fkey");
        });

        modelBuilder.Entity<Pedido>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Pedido_pkey");

            entity.ToTable("Pedido");

            entity.HasIndex(e => new { e.NegocioId, e.CreatedAt }, "Pedido_negocioId_createdAt_idx");

            entity.HasIndex(e => e.NegocioId, "Pedido_negocioId_idx");

            entity.HasIndex(e => new { e.NegocioId, e.Estado }, "Pedido_negocioId_estado_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ApellidoCliente).HasColumnName("apellidoCliente");
            entity.Property(e => e.ClienteId).HasColumnName("clienteId");
            entity.Property(e => e.CostoEnvio).HasColumnName("costoEnvio");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.CuentaAbierta).HasColumnName("cuentaAbierta");
            entity.Property(e => e.DemoraEstimadaMin).HasColumnName("demoraEstimadaMin");
            entity.Property(e => e.Departamento).HasColumnName("departamento");
            entity.Property(e => e.Direccion).HasColumnName("direccion");
            entity.Property(e => e.DireccionFormateada).HasColumnName("direccionFormateada");
            entity.Property(e => e.DireccionLat).HasColumnName("direccionLat");
            entity.Property(e => e.DireccionLng).HasColumnName("direccionLng");
            entity.Property(e => e.DireccionPrecision).HasColumnName("direccionPrecision");
            entity.Property(e => e.MesaId).HasColumnName("mesaId");
            entity.Property(e => e.MotivoCancelacion).HasColumnName("motivoCancelacion");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.NombreCliente).HasColumnName("nombreCliente");
            entity.Property(e => e.NotasRepartidor).HasColumnName("notasRepartidor");
            entity.Property(e => e.NumeroCliente).HasColumnName("numeroCliente");
            entity.Property(e => e.Piso).HasColumnName("piso");
            entity.Property(e => e.Referencias).HasColumnName("referencias");
            entity.Property(e => e.RepartidorId).HasColumnName("repartidorId");
            entity.Property(e => e.ShippingReason).HasColumnName("shippingReason");
            entity.Property(e => e.ShippingZoneName).HasColumnName("shippingZoneName");
            entity.Property(e => e.Total).HasColumnName("total");
            entity.Property(e => e.Tipo)
                .HasDefaultValueSql("'LOCAL'::\"TipoPedido\"")
                .HasColumnName("tipo");
            entity.Property(e => e.Estado)
                .HasDefaultValueSql("'PENDIENTE'::\"EstadoPedido\"")
                .HasColumnName("estado");
            entity.Property(e => e.EstadoPago)
                .HasDefaultValueSql("'PENDIENTE'::\"EstadoPago\"")
                .HasColumnName("estadoPago");
            entity.Property(e => e.MetodoPago).HasColumnName("metodoPago");
            entity.Property(e => e.CanceladoPor).HasColumnName("canceladoPor");

            entity.HasOne(d => d.Cliente).WithMany(p => p.Pedidos)
                .HasForeignKey(d => d.ClienteId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Pedido_clienteId_fkey");

            entity.HasOne(d => d.MesaNavigation).WithMany(p => p.Pedidos)
                .HasForeignKey(d => d.MesaId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Pedido_mesaId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Pedidos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Pedido_negocioId_fkey");

            entity.HasOne(d => d.Repartidor).WithMany(p => p.Pedidos)
                .HasForeignKey(d => d.RepartidorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Pedido_repartidorId_fkey");
        });

        modelBuilder.Entity<PedidoDetalle>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PedidoDetalle_pkey");

            entity.ToTable("PedidoDetalle");

            entity.HasIndex(e => e.NegocioId, "PedidoDetalle_negocioId_idx");

            entity.HasIndex(e => new { e.NegocioId, e.PedidoId }, "PedidoDetalle_negocioId_pedidoId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Cantidad).HasColumnName("cantidad");
            entity.Property(e => e.Extras)
                .HasColumnType("jsonb")
                .HasColumnName("extras");
            entity.Property(e => e.ImpresoEnCocina).HasColumnName("impresoEnCocina");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Notas).HasColumnName("notas");
            entity.Property(e => e.PedidoId).HasColumnName("pedidoId");
            entity.Property(e => e.PrecioUnitario).HasColumnName("precioUnitario");
            entity.Property(e => e.ProductoId).HasColumnName("productoId");
            entity.Property(e => e.SinExtras).HasColumnName("sinExtras");
            entity.Property(e => e.Subtotal).HasColumnName("subtotal");

            entity.HasOne(d => d.Negocio).WithMany(p => p.PedidoDetalles)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoDetalle_negocioId_fkey");

            entity.HasOne(d => d.Pedido).WithMany(p => p.PedidoDetalles)
                .HasForeignKey(d => d.PedidoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoDetalle_pedidoId_fkey");

            entity.HasOne(d => d.Producto).WithMany(p => p.PedidoDetalles)
                .HasForeignKey(d => d.ProductoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoDetalle_productoId_fkey");
        });

        modelBuilder.Entity<PedidoOfertum>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PedidoOferta_pkey");

            entity.HasIndex(e => e.OfertaId, "PedidoOferta_ofertaId_idx");

            entity.HasIndex(e => e.PedidoDetalleId, "PedidoOferta_pedidoDetalleId_key").IsUnique();

            entity.HasIndex(e => e.PedidoId, "PedidoOferta_pedidoId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.DescuentoAplicado).HasColumnName("descuentoAplicado");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.OfertaId).HasColumnName("ofertaId");
            entity.Property(e => e.PedidoDetalleId).HasColumnName("pedidoDetalleId");
            entity.Property(e => e.PedidoId).HasColumnName("pedidoId");
            entity.Property(e => e.PrecioFinal).HasColumnName("precioFinal");
            entity.Property(e => e.PrecioOriginal).HasColumnName("precioOriginal");

            entity.HasOne(d => d.Negocio).WithMany(p => p.PedidoOferta)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoOferta_negocioId_fkey");

            entity.HasOne(d => d.Oferta).WithMany(p => p.PedidoOferta)
                .HasForeignKey(d => d.OfertaId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoOferta_ofertaId_fkey");

            entity.HasOne(d => d.PedidoDetalle).WithOne(p => p.PedidoOfertum)
                .HasForeignKey<PedidoOfertum>(d => d.PedidoDetalleId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("PedidoOferta_pedidoDetalleId_fkey");

            entity.HasOne(d => d.Pedido).WithMany(p => p.PedidoOferta)
                .HasForeignKey(d => d.PedidoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PedidoOferta_pedidoId_fkey");
        });

        modelBuilder.Entity<PizzaMediaMedium>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PizzaMediaMedia_pkey");

            entity.HasIndex(e => e.PedidoDetalleId, "PizzaMediaMedia_pedidoDetalleId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.PedidoDetalleId).HasColumnName("pedidoDetalleId");
            entity.Property(e => e.Sabor1Id).HasColumnName("sabor1Id");
            entity.Property(e => e.Sabor2Id).HasColumnName("sabor2Id");

            entity.HasOne(d => d.Negocio).WithMany(p => p.PizzaMediaMedia)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PizzaMediaMedia_negocioId_fkey");

            entity.HasOne(d => d.PedidoDetalle).WithOne(p => p.PizzaMediaMedium)
                .HasForeignKey<PizzaMediaMedium>(d => d.PedidoDetalleId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("PizzaMediaMedia_pedidoDetalleId_fkey");
        });

        modelBuilder.Entity<PrismaMigration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("_prisma_migrations_pkey");

            entity.ToTable("_prisma_migrations");

            entity.Property(e => e.Id)
                .HasMaxLength(36)
                .HasColumnName("id");
            entity.Property(e => e.AppliedStepsCount).HasColumnName("applied_steps_count");
            entity.Property(e => e.Checksum)
                .HasMaxLength(64)
                .HasColumnName("checksum");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
            entity.Property(e => e.Logs).HasColumnName("logs");
            entity.Property(e => e.MigrationName)
                .HasMaxLength(255)
                .HasColumnName("migration_name");
            entity.Property(e => e.RolledBackAt).HasColumnName("rolled_back_at");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("started_at");
        });

        modelBuilder.Entity<Producto>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Producto_pkey");

            entity.ToTable("Producto");

            entity.HasIndex(e => new { e.Codigo, e.NegocioId }, "Producto_codigo_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AceptaSalsas)
                .HasDefaultValue(true)
                .HasColumnName("aceptaSalsas");
            entity.Property(e => e.AceptaToppings)
                .HasDefaultValue(true)
                .HasColumnName("aceptaToppings");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.Badge).HasColumnName("badge");
            entity.Property(e => e.CategoriaId).HasColumnName("categoriaId");
            entity.Property(e => e.Codigo).HasColumnName("codigo");
            entity.Property(e => e.Descripcion).HasColumnName("descripcion");
            entity.Property(e => e.EsParaVenta)
                .HasDefaultValue(true)
                .HasColumnName("esParaVenta");
            entity.Property(e => e.EsVegetariano).HasColumnName("esVegetariano");
            entity.Property(e => e.ImagenUrl).HasColumnName("imagenUrl");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.PermitirMediaMedia).HasColumnName("permitirMediaMedia");
            entity.Property(e => e.Precio).HasColumnName("precio");
            entity.Property(e => e.TiempoPreparacionMin).HasColumnName("tiempoPreparacionMin");
            entity.Property(e => e.ToppingGruposCompatibles)
                .HasColumnType("jsonb")
                .HasColumnName("toppingGruposCompatibles");

            entity.HasOne(d => d.Categoria).WithMany(p => p.Productos)
                .HasForeignKey(d => d.CategoriaId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Producto_categoriaId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Productos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Producto_negocioId_fkey");
        });

        modelBuilder.Entity<ProductoRecetum>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ProductoReceta_pkey");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Cantidad).HasColumnName("cantidad");
            entity.Property(e => e.InsumoId).HasColumnName("insumoId");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.ProductoId).HasColumnName("productoId");

            entity.HasOne(d => d.Insumo).WithMany(p => p.ProductoReceta)
                .HasForeignKey(d => d.InsumoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ProductoReceta_insumoId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.ProductoReceta)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ProductoReceta_negocioId_fkey");

            entity.HasOne(d => d.Producto).WithMany(p => p.ProductoReceta)
                .HasForeignKey(d => d.ProductoId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ProductoReceta_productoId_fkey");
        });

        modelBuilder.Entity<Proveedor>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Proveedor_pkey");

            entity.ToTable("Proveedor");

            entity.HasIndex(e => new { e.Nombre, e.NegocioId }, "Proveedor_nombre_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Notas).HasColumnName("notas");
            entity.Property(e => e.Telefono).HasColumnName("telefono");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Proveedors)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Proveedor_negocioId_fkey");
        });

        // TempToken: tabla de sistema (one-time tokens, hoy sólo el OTT de Google OAuth). SIN Global Query
        // Filter, igual que RefreshToken. Agregada post-scaffold; reaplicar si se vuelve a scaffoldear.
        modelBuilder.Entity<TempToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("TempToken_pkey");

            entity.ToTable("TempToken");

            entity.HasIndex(e => e.ExpiresAt, "TempToken_expiresAt_idx");

            entity.HasIndex(e => e.TokenHash, "TempToken_tokenHash_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ExpiresAt)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("expiresAt");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.TokenHash).HasColumnName("tokenHash");
            entity.Property(e => e.Usada).HasColumnName("usada");
            entity.Property(e => e.UserId).HasColumnName("userId");

            // FK a User sin navegación inversa (no ensuciamos el modelo User con una colección más).
            entity.HasOne(d => d.User).WithMany()
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("TempToken_userId_fkey");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RefreshToken_pkey");

            entity.ToTable("RefreshToken");

            entity.HasIndex(e => e.ExpiresAt, "RefreshToken_expiresAt_idx");

            entity.HasIndex(e => e.TokenHash, "RefreshToken_tokenHash_key").IsUnique();

            entity.HasIndex(e => e.UserId, "RefreshToken_userId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ExpiresAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("expiresAt");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Revocado).HasColumnName("revocado");
            entity.Property(e => e.TokenHash).HasColumnName("tokenHash");
            entity.Property(e => e.UserId).HasColumnName("userId");

            entity.HasOne(d => d.User).WithMany(p => p.RefreshTokens)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("RefreshToken_userId_fkey");
        });

        modelBuilder.Entity<StockMovimiento>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("StockMovimiento_pkey");

            entity.ToTable("StockMovimiento");

            entity.HasIndex(e => e.AderezoId, "StockMovimiento_aderezoId_idx");

            entity.HasIndex(e => e.CreatedAt, "StockMovimiento_createdAt_idx");

            entity.HasIndex(e => e.ExtraId, "StockMovimiento_extraId_idx");

            entity.HasIndex(e => e.InsumoId, "StockMovimiento_insumoId_idx");

            entity.HasIndex(e => new { e.NegocioId, e.CreatedAt }, "StockMovimiento_negocioId_createdAt_idx");

            entity.HasIndex(e => e.NegocioId, "StockMovimiento_negocioId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AderezoId).HasColumnName("aderezoId");
            entity.Property(e => e.Cantidad).HasColumnName("cantidad");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ExtraId).HasColumnName("extraId");
            entity.Property(e => e.InsumoId).HasColumnName("insumoId");
            entity.Property(e => e.Motivo).HasColumnName("motivo");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.PedidoId).HasColumnName("pedidoId");
            entity.Property(e => e.StockAntes).HasColumnName("stockAntes");
            entity.Property(e => e.StockDespues).HasColumnName("stockDespues");
            entity.Property(e => e.Tipo).HasColumnName("tipo");
            entity.Property(e => e.UserId).HasColumnName("userId");

            entity.HasOne(d => d.Insumo).WithMany(p => p.StockMovimientos)
                .HasForeignKey(d => d.InsumoId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("StockMovimiento_insumoId_fkey");

            entity.HasOne(d => d.Negocio).WithMany(p => p.StockMovimientos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("StockMovimiento_negocioId_fkey");
        });

        modelBuilder.Entity<ToppingGrupo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ToppingGrupo_pkey");

            entity.ToTable("ToppingGrupo");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.EsIncluido)
                .HasDefaultValue(true)
                .HasColumnName("esIncluido");
            entity.Property(e => e.MaxExtrasGratis)
                .HasDefaultValue(3)
                .HasColumnName("maxExtrasGratis");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Orden).HasColumnName("orden");

            entity.HasOne(d => d.Negocio).WithMany(p => p.ToppingGrupos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("ToppingGrupo_negocioId_fkey");
        });

        modelBuilder.Entity<Turno>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Turno_pkey");

            entity.ToTable("Turno");

            entity.HasIndex(e => e.HoraInicio, "Turno_horaInicio_idx");

            entity.HasIndex(e => e.UserId, "Turno_userId_idx");

            entity.HasIndex(e => e.Tipo, "Turno_tipo_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CajaAperturaMonto).HasColumnName("cajaAperturaMonto");
            entity.Property(e => e.CajaCierreMonto).HasColumnName("cajaCierreMonto");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.HoraFin)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("horaFin");
            entity.Property(e => e.HoraInicio)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("horaInicio");
            entity.Property(e => e.MontoEsperado).HasColumnName("montoEsperado");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Notas).HasColumnName("notas");
            entity.Property(e => e.UserId).HasColumnName("userId");
            entity.Property(e => e.VentasTotales).HasColumnName("ventasTotales");
            entity.Property(e => e.Tipo)
                .HasDefaultValueSql("'APERTURA'::\"TipoTurno\"")
                .HasColumnName("tipo");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Turnos)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Turno_negocioId_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.Turnos)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Turno_userId_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("User_pkey");

            entity.ToTable("User");

            entity.HasIndex(e => new { e.Email, e.NegocioId }, "User_email_negocioId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Activo)
                .HasDefaultValue(true)
                .HasColumnName("activo");
            entity.Property(e => e.BloqueadoHasta)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("bloqueadoHasta");
            entity.Property(e => e.BloqueadoLoginHasta)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("bloqueadoLoginHasta");
            entity.Property(e => e.CodigoExpira)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("codigoExpira");
            entity.Property(e => e.CodigoVerificacion).HasColumnName("codigoVerificacion");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.EmailVerificado).HasColumnName("emailVerificado");
            entity.Property(e => e.IntentosLogin).HasColumnName("intentosLogin");
            entity.Property(e => e.IntentosVerificacion).HasColumnName("intentosVerificacion");
            entity.Property(e => e.NegocioId).HasColumnName("negocioId");
            entity.Property(e => e.Nombre).HasColumnName("nombre");
            entity.Property(e => e.Password).HasColumnName("password");
            entity.Property(e => e.Role)
                .HasDefaultValueSql("'TRABAJADOR'::\"Role\"")
                .HasColumnName("role");

            entity.HasOne(d => d.Negocio).WithMany(p => p.Users)
                .HasForeignKey(d => d.NegocioId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("User_negocioId_fkey");
        });

        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("WebhookEvent_pkey");

            entity.ToTable("WebhookEvent");

            entity.HasIndex(e => e.ExternalId, "WebhookEvent_externalId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ExternalId).HasColumnName("externalId");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
