using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests;

/// <summary>
/// Fixture compartido por la colección de tests de integración. Levanta una base
/// dedicada <c>orbit_csharp_test</c> en el PostgreSQL local con <c>EnsureCreated()</c>,
/// la siembra con dos negocios (tenants) y la elimina al terminar. Sin Docker.
///
/// La cadena de conexión se puede sobreescribir con la variable de entorno
/// <c>ORBIT_TEST_CONNECTION</c>; si no, usa el localhost/postgres por defecto.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultTestConnection =
        "Host=localhost;Port=5432;Database=orbit_csharp_test;Username=postgres;Password=lauri2001";

    public const string NegocioAId = "neg-a";
    public const string NegocioBId = "neg-b";

    private readonly NpgsqlDataSource _dataSource;

    public DatabaseFixture()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ORBIT_TEST_CONNECTION") ?? DefaultTestConnection;

        // Mismo mapeo de enums que la Api (ExactNameTranslator) para que EnsureCreated
        // genere los tipos enum de PostgreSQL idénticos a los del modelo.
        var enumNameTranslator = new ExactNameTranslator();
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder
            .MapEnum<EstadoMesa>(nameTranslator: enumNameTranslator)
            .MapEnum<EstadoOferta>(nameTranslator: enumNameTranslator)
            .MapEnum<EstadoPago>(nameTranslator: enumNameTranslator)
            .MapEnum<EstadoPedido>(nameTranslator: enumNameTranslator)
            .MapEnum<MetodoPago>(nameTranslator: enumNameTranslator)
            .MapEnum<Role>(nameTranslator: enumNameTranslator)
            .MapEnum<TipoMovimientoCaja>(nameTranslator: enumNameTranslator)
            .MapEnum<TipoOferta>(nameTranslator: enumNameTranslator)
            .MapEnum<TipoPedido>(nameTranslator: enumNameTranslator)
            .MapEnum<TipoTurno>(nameTranslator: enumNameTranslator)
            .MapEnum<UnidadMedida>(nameTranslator: enumNameTranslator);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <summary>
    /// Crea un <see cref="OrbitDbContext"/> apuntando a la base de test con el tenant
    /// indicado. <paramref name="negocioId"/> en <c>null</c> simula "sin tenant".
    /// </summary>
    public OrbitDbContext CreateContext(string? negocioId)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(_dataSource, npgsql => npgsql.MapOrbitEnums())
            .Options;
        return new OrbitDbContext(options, new SettableTenantProvider(negocioId));
    }

    public async Task InitializeAsync()
    {
        // El tenant del contexto de seeding es irrelevante: los INSERT no pasan por los
        // query filters (sólo afectan lecturas). Usamos null para dejarlo explícito.
        await using var ctx = CreateContext(negocioId: null);

        await ctx.Database.EnsureDeletedAsync();
        await ctx.Database.EnsureCreatedAsync();

        await SeedAsync(ctx);
    }

    public async Task DisposeAsync()
    {
        await using (var ctx = CreateContext(negocioId: null))
        {
            await ctx.Database.EnsureDeletedAsync();
        }

        await _dataSource.DisposeAsync();
    }

    private static async Task SeedAsync(OrbitDbContext ctx)
    {
        // Las columnas timestamp del modelo son "without time zone"; Npgsql rechaza
        // DateTime con Kind=Utc para ese tipo, así que usamos Kind=Unspecified.
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        ctx.Negocios.AddRange(
            NewNegocio(NegocioAId, "Negocio A", "negocio-a", now),
            NewNegocio(NegocioBId, "Negocio B", "negocio-b", now));

        // Negocio A: 2 productos + 1 cliente.
        ctx.Productos.AddRange(
            NewProducto("prod-a1", NegocioAId, "Pizza A1", 1000),
            NewProducto("prod-a2", NegocioAId, "Pizza A2", 1200));
        ctx.Clientes.Add(NewCliente("cli-a1", NegocioAId, "Cliente A1", "111", now));

        // Negocio B: 1 producto + 1 cliente.
        ctx.Productos.Add(NewProducto("prod-b1", NegocioBId, "Pizza B1", 1500));
        ctx.Clientes.Add(NewCliente("cli-b1", NegocioBId, "Cliente B1", "222", now));

        await ctx.SaveChangesAsync();
    }

    private static Negocio NewNegocio(string id, string nombre, string slug, DateTime now) => new()
    {
        Id = id,
        Nombre = nombre,
        Slug = slug,
        Activo = true,
        Plan = "basic",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Producto NewProducto(string id, string negocioId, string nombre, double precio) => new()
    {
        Id = id,
        NegocioId = negocioId,
        Nombre = nombre,
        Precio = precio,
    };

    private static Cliente NewCliente(string id, string negocioId, string nombre, string telefono, DateTime now) => new()
    {
        Id = id,
        NegocioId = negocioId,
        Nombre = nombre,
        Telefono = telefono,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

/// <summary>
/// Colección que comparte un único <see cref="DatabaseFixture"/> entre todos los tests,
/// de modo que la base de test se crea/siembra/elimina una sola vez.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database collection";
}
