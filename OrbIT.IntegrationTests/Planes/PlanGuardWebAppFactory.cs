using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Planes;

/// <summary>
/// Levanta la Api in-memory para los tests del <c>IPlanGuard</c> + <c>UsuariosController</c>, contra una base
/// dedicada <c>orbit_csharp_planguardtest</c>. Mismo patrón que las demás factories (config por env vars,
/// colección sin paralelización para no pisar esas env vars globales).
/// </summary>
public sealed class PlanGuardWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_planguardtest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public PlanGuardWebAppFactory()
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
public sealed class PlanGuardApiCollection
{
    public const string Name = "PlanGuard Api collection";
}
