using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Plano;

/// <summary>
/// Levanta la Api in-memory para los tests de integración del <c>PlanoController</c>, contra una base
/// dedicada <c>orbit_csharp_planotest</c>. Mismo patrón que las demás factories: config por env vars y
/// colección sin paralelización (esas env vars son globales al proceso).
/// </summary>
public sealed class PlanoWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_planotest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public PlanoWebAppFactory()
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

/// <summary>Colección sin paralelización (las factories inyectan la connection string por env var global).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlanoApiCollection
{
    public const string Name = "Plano Api collection";
}
