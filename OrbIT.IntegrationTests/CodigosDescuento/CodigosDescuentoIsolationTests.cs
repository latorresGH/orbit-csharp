using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.CodigosDescuento;

/// <summary>
/// Test de integración end-to-end del <c>CodigosDescuentoController</c> + <c>CodigosDescuentoService</c>.
/// Cubre aislamiento multi-tenant + unicidad de código (409), y los casos de la validación pública:
/// válido, código inexistente, producto incorrecto, vencido y agotado por usos.
/// </summary>
[Collection(CodigoDescuentoApiCollection.Name)]
public sealed class CodigosDescuentoIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-cd-a";
    private const string NegocioBId = "neg-cd-b";
    private const string NegocioASlug = "negocio-cd-a";
    private const string NegocioBSlug = "negocio-cd-b";
    private const string AdminAEmail = "admin-a@cd.test";
    private const string AdminBEmail = "admin-b@cd.test";

    private const string PizzaId = "prod-cd-pizza";
    private const string EmpanadaId = "prod-cd-emp";

    private readonly CodigoDescuentoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio CD A", NegocioASlug, "user-cd-a", AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio CD B", NegocioBSlug, "user-cd-b", AdminBEmail, now);

        db.Productos.Add(new Producto { Id = PizzaId, Nombre = "Pizza", Precio = 200, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = EmpanadaId, Nombre = "Empanada", Precio = 100, NegocioId = NegocioAId });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await db.Database.EnsureDeletedAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Aislamiento_multitenant_y_unicidad_de_codigo()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var codigoA = await CrearAsync(clientA, new
        {
            codigo = "promo",
            tipoDescuento = "PORCENTAJE",
            valor = 15,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
        });
        Assert.Equal("PROMO", codigoA.Codigo); // normalizado UPPER

        // Mismo código (case-insensitive) en el mismo negocio → 409.
        var dup = await clientA.PostAsJsonAsync("/codigos-descuento", new
        {
            codigo = "PROMO",
            tipoDescuento = "MONTO_FIJO",
            valor = 100,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // B sí puede usar "PROMO" (otro negocio).
        var codigoB = await clientB.PostAsJsonAsync("/codigos-descuento", new
        {
            codigo = "PROMO",
            tipoDescuento = "PORCENTAJE",
            valor = 20,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
        });
        Assert.Equal(HttpStatusCode.Created, codigoB.StatusCode);

        // A no ve el código de B por id → 404.
        var creadoB = (await codigoB.Content.ReadFromJsonAsync<CodigoDto>())!;
        var cross = await clientA.GetAsync($"/codigos-descuento/{creadoB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Porcentaje fuera de rango → 400.
        var malPct = await clientA.PostAsJsonAsync("/codigos-descuento", new
        {
            codigo = "BADPCT",
            tipoDescuento = "PORCENTAJE",
            valor = 150,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
        });
        Assert.Equal(HttpStatusCode.BadRequest, malPct.StatusCode);
    }

    [Fact]
    public async Task Validar_valido_inexistente_y_producto_incorrecto()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        await CrearAsync(clientA, new
        {
            codigo = "DESC10",
            tipoDescuento = "PORCENTAJE",
            valor = 10,
            productoId = PizzaId,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
        });

        var anon = _factory.CreateClient();

        // Válido (con el producto correcto).
        var ok = await ValidarAsync(anon, new { codigo = "desc10", productoId = PizzaId });
        Assert.True(ok.Valido);
        Assert.Equal(10, ok.Descuento!.Valor);

        // Código inexistente → valido:false (200, no error HTTP).
        var noExiste = await ValidarAsync(anon, new { codigo = "NOPE" });
        Assert.False(noExiste.Valido);
        Assert.Equal("Código no válido", noExiste.Error);

        // Producto incorrecto → no aplica.
        var productoMalo = await ValidarAsync(anon, new { codigo = "DESC10", productoId = EmpanadaId });
        Assert.False(productoMalo.Valido);
        Assert.Contains("producto", productoMalo.Error!);
    }

    [Fact]
    public async Task Validar_vencido_y_agotado()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // Vencido (fechaFin en el pasado).
        await CrearAsync(clientA, new
        {
            codigo = "VIEJO",
            tipoDescuento = "MONTO_FIJO",
            valor = 50,
            fechaInicio = "2020-01-01",
            fechaFin = "2020-12-31",
        });

        // Agotado (usosMaximos = 1, lo dejamos en su tope).
        var limitado = await CrearAsync(clientA, new
        {
            codigo = "LIMITADO",
            tipoDescuento = "MONTO_FIJO",
            valor = 50,
            fechaInicio = "2020-01-01",
            fechaFin = "2099-12-31",
            usosMaximos = 1,
        });
        await UsingDbAsync(async db =>
        {
            await db.CodigoDescuentos.IgnoreQueryFilters().Where(c => c.Id == limitado.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsosActuales, 1));
        });

        var anon = _factory.CreateClient();

        var vencido = await ValidarAsync(anon, new { codigo = "VIEJO" });
        Assert.False(vencido.Valido);
        Assert.Contains("venció", vencido.Error!);

        var agotado = await ValidarAsync(anon, new { codigo = "LIMITADO" });
        Assert.False(agotado.Valido);
        Assert.Contains("máximo de usos", agotado.Error!);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task UsingDbAsync(Func<OrbitDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        await action(db);
    }

    private static async Task<CodigoDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/codigos-descuento", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CodigoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private async Task<ValidacionDto> ValidarAsync(HttpClient anon, object body)
    {
        var response = await anon.PostAsJsonAsync($"/codigos-descuento/validar?negocio={NegocioASlug}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ValidacionDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocioConAdmin(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, DateTime now)
    {
        db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = negocioNombre,
            Slug = slug,
            Activo = true,
            Plan = "basic",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            Password = hasher.Hash(Password),
            Nombre = "Admin",
            Role = Role.ADMIN,
            Activo = true,
            NegocioId = negocioId,
            EmailVerificado = true,
            CreatedAt = now,
        });
    }

    private sealed record CodigoDto(string Id, string Codigo, string TipoDescuento, double Valor, bool Activo);

    private sealed record ValidacionDto(bool Valido, string? Error, DescuentoDto? Descuento);

    private sealed record DescuentoDto(string Tipo, double Valor);
}
