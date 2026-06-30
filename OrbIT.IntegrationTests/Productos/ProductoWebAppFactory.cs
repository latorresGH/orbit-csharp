using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Productos;

/// <summary>
/// Levanta la Api in-memory para los tests de integración de productos, apuntando a una base dedicada
/// <c>orbit_csharp_productest</c> y con secrets de JWT de test. Mismo patrón que las demás factories:
/// la config se inyecta por variables de entorno y la colección corre con <c>DisableParallelization</c>.
/// </summary>
public sealed class ProductoWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_productest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public ProductoWebAppFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__AccessTokenSecret", AccessSecret);
        Environment.SetEnvironmentVariable("Jwt__RefreshTokenSecret", RefreshSecret);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("Jwt__AccessTokenSecret", null);
            Environment.SetEnvironmentVariable("Jwt__RefreshTokenSecret", null);
        }
    }
}

/// <summary>Colección con paralelización deshabilitada (inyecta env vars globales del proceso).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductoApiCollection
{
    public const string Name = "Productos Api collection";
}
