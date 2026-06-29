using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Auth;

/// <summary>
/// Levanta la Api in-memory para los tests de integración de auth, apuntando a una base
/// dedicada <c>orbit_csharp_authtest</c> y con secrets de JWT de test.
///
/// La config se inyecta por variables de entorno (no por <c>ConfigureAppConfiguration</c>)
/// porque <c>Program.cs</c> lee la connection string y arma el <c>NpgsqlDataSource</c>
/// ANTES de <c>builder.Build()</c>; sólo las env vars de <c>CreateDefaultBuilder</c> ya están
/// disponibles en ese punto y tienen prioridad sobre appsettings.
/// </summary>
public sealed class AuthWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_authtest;Username=postgres;Password=lauri2001";

    // Secrets de test (≥ 32 bytes, distintos entre sí). No son los de producción.
    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public AuthWebAppFactory()
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
