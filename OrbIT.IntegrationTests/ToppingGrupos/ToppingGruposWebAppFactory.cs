using Microsoft.AspNetCore.Mvc.Testing;

namespace OrbIT.IntegrationTests.ToppingGrupos;

/// <summary>
/// Levanta la Api in-memory para los tests de integración de grupos de toppings, apuntando a una
/// base dedicada <c>orbit_csharp_toppingtest</c> y con secrets de JWT de test. Mismo patrón que
/// <see cref="Categorias.CategoriasWebAppFactory"/>: la config se inyecta por variables de entorno
/// (Program.cs arma el NpgsqlDataSource antes de builder.Build()), y la colección de tests corre
/// con <c>DisableParallelization</c> para no pisar esas env vars globales con otros factories.
/// </summary>
public sealed class ToppingGruposWebAppFactory : WebApplicationFactory<Program>
{
    public const string ConnectionString =
        "Host=localhost;Port=5432;Database=orbit_csharp_toppingtest;Username=postgres;Password=lauri2001";

    private const string AccessSecret = "test-access-secret__orbit__0123456789ABCDEF";
    private const string RefreshSecret = "test-refresh-secret__orbit__ZYXWVU9876543210";

    public ToppingGruposWebAppFactory()
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
/// Colección con paralelización deshabilitada (igual que la de categorías): los tests basados en
/// <see cref="WebApplicationFactory{TEntryPoint}"/> inyectan la connection string por variable de
/// entorno global y no deben correr en paralelo con otros factories.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToppingGruposApiCollection
{
    public const string Name = "ToppingGrupos Api collection";
}
