using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Turnos;

/// <summary>
/// Levanta la Api in-memory para los tests de integración de turnos, base dedicada
/// <c>orbit_csharp_turnotest</c>. Mismo patrón que las demás factories.
/// </summary>
public sealed class TurnoWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_turnotest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public TurnoWebAppFactory()
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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TurnoApiCollection
{
    public const string Name = "Turnos Api collection";
}
