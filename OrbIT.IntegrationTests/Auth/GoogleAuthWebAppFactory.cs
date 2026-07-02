using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Auth;

/// <summary>
/// Levanta la Api in-memory para los tests de Google OAuth, base dedicada <c>orbit_csharp_googletest</c>.
/// No define credenciales de Google: el handler no se registra (TieneCredenciales = false), por eso estos tests
/// ejercen las piezas aisladas (OTT + registro), no el roundtrip real con Google.
/// </summary>
public sealed class GoogleAuthWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_googletest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public GoogleAuthWebAppFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__AccessTokenSecret", AccessSecret);
        Environment.SetEnvironmentVariable("Jwt__RefreshTokenSecret", RefreshSecret);
        // Aseguramos que no haya credenciales reales de Google en el entorno de test (el handler no se registra).
        Environment.SetEnvironmentVariable("Google__ClientId", "__TEST_NO_GOOGLE__");
        Environment.SetEnvironmentVariable("Google__ClientSecret", "__TEST_NO_GOOGLE__");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("Jwt__AccessTokenSecret", null);
            Environment.SetEnvironmentVariable("Jwt__RefreshTokenSecret", null);
            Environment.SetEnvironmentVariable("Google__ClientId", null);
            Environment.SetEnvironmentVariable("Google__ClientSecret", null);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoogleAuthApiCollection
{
    public const string Name = "GoogleAuth Api collection";
}
