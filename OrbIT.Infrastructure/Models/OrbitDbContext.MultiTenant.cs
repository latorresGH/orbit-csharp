using Microsoft.EntityFrameworkCore;
using OrbIT.Domain.MultiTenancy;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Capa multi-tenant del <see cref="OrbitDbContext"/>. Aplica global query filters por
/// negocio para que toda lectura quede automáticamente acotada al tenant activo
/// (<see cref="ITenantProvider.NegocioId"/>), sin tener que filtrar a mano en cada query.
///
/// El <see cref="ITenantProvider"/> se inyecta por constructor y se lee en tiempo de
/// ejecución de cada query (EF parametriza el filtro), por lo que un mismo modelo cacheado
/// sirve para cualquier tenant. Para saltarse el filtro de forma explícita (reportes
/// cross-negocio, seeding, tests) usar <c>.IgnoreQueryFilters()</c>.
///
/// Pendiente para una iteración aparte (no implementado aquí): stamping automático de
/// NegocioId en INSERT y validación de pertenencia en UPDATE/DELETE vía override de
/// SaveChanges. Primero se consolida el filtro de lectura.
/// </summary>
public partial class OrbitDbContext
{
    private readonly ITenantProvider? _tenantProvider;

    /// <summary>
    /// Constructor usado en runtime (vía DI): además de las opciones recibe el
    /// <see cref="ITenantProvider"/> que alimenta los query filters. El contenedor de DI
    /// elige este constructor por ser el más específico que puede satisfacer.
    /// </summary>
    public OrbitDbContext(DbContextOptions<OrbitDbContext> options, ITenantProvider tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Negocio se filtra por su propia Id (el tenant es el propio registro de negocio).
        modelBuilder.Entity<Negocio>().HasQueryFilter(e => e.Id == _tenantProvider!.NegocioId);

        // 35 entidades hijas: se filtran por su NegocioId contra el tenant activo.
        modelBuilder.Entity<Aderezo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<AderezoCategorium>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<AderezoConsumo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<AderezoPrecio>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<AuditLog>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Barrio>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<CajaMovimiento>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Categorium>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Cliente>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<CodigoDescuento>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Configuracion>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<DemoraConfig>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Extra>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<ExtraCategorium>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<ExtraConsumo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<ExtraPrecio>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<GastoOperativo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<GrupoCombo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<GrupoOpcion>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Insumo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Mesa>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<OfertaProducto>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Ofertum>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Pedido>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<PedidoDetalle>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<PedidoOfertum>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<PizzaMediaMedium>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Producto>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<ProductoRecetum>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Proveedor>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<StockMovimiento>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<ToppingGrupo>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<Turno>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
        modelBuilder.Entity<User>().HasQueryFilter(e => e.NegocioId == _tenantProvider!.NegocioId);
    }
}
