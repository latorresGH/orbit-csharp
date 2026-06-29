using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.Categorias;

/// <summary>
/// Levanta la Api in-memory para los tests de integración de categorías, apuntando a una
/// base dedicada <c>orbit_csharp_categoriastest</c> y con secrets de JWT de test.
///
/// Misma estrategia que <see cref="Auth.AuthWebAppFactory"/>: la config se inyecta por
/// variables de entorno porque <c>Program.cs</c> arma el <c>NpgsqlDataSource</c> antes de
/// <c>builder.Build()</c>. Para que esa inyección por env vars (global al proceso) no choque
/// con la del factory de auth, el test que usa este factory corre en una colección con
/// <c>DisableParallelization</c>.
/// </summary>
public sealed class CategoriasWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_categoriastest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public CategoriasWebAppFactory()
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

/// <summary>
/// Colección con paralelización deshabilitada: los tests basados en
/// <see cref="WebApplicationFactory{TEntryPoint}"/> inyectan la connection string por
/// variable de entorno global, así que no deben correr en paralelo con otros factories.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CategoriasApiCollection
{
    public const string Name = "Categorias Api collection";
}
