using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Gastos;

/// <summary>
/// Tests de integración de Gastos Operativos: CRUD ADMIN-only, validación de categoría, listado paginado,
/// resumen agregado (GroupBy server-side) y aislamiento multi-tenant.
/// </summary>
[Collection(GastosApiCollection.Name)]
public sealed class GastosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";
    private const string NegocioAId = "neg-gas-a";
    private const string NegocioBId = "neg-gas-b";
    private const string NegocioASlug = "negocio-gas-a";
    private const string NegocioBSlug = "negocio-gas-b";
    private const string AdminAEmail = "admin-a@gas.test";
    private const string TrabajadorAEmail = "trab-a@gas.test";
    private const string AdminBEmail = "admin-b@gas.test";

    private readonly GastosWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio GAS A", Slug = NegocioASlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Negocios.Add(new Negocio { Id = NegocioBId, Nombre = "Negocio GAS B", Slug = NegocioBSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = "user-gas-a", Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-gas-a-trab", Email = TrabajadorAEmail, Password = hasher.Hash(Password), Nombre = "Trabajador A", Role = Role.TRABAJADOR, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-gas-b", Email = AdminBEmail, Password = hasher.Hash(Password), Nombre = "Admin B", Role = Role.ADMIN, Activo = true, NegocioId = NegocioBId, EmailVerificado = true, CreatedAt = now });

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
    public async Task Crear_valida_categoria_y_obtiene()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var invalido = await client.PostAsJsonAsync("/gastos", new { categoria = "CRIPTO", monto = 1000 });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        var resp = await client.PostAsJsonAsync("/gastos", new { categoria = "LUZ", monto = 15000, descripcion = "Factura mayo" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var gasto = (await resp.Content.ReadFromJsonAsync<GastoDto>())!;
        Assert.Equal("LUZ", gasto.Categoria);
        Assert.Equal(15000, gasto.Monto);

        var leido = (await client.GetFromJsonAsync<GastoDto>($"/gastos/{gasto.Id}"))!;
        Assert.Equal(gasto.Id, leido.Id);
        Assert.Equal("Factura mayo", leido.Descripcion);
    }

    [Fact]
    public async Task Listar_paginado_y_filtro_categoria()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        await CrearAsync(client, "LUZ", 1000);
        await CrearAsync(client, "GAS", 2000);
        await CrearAsync(client, "GAS", 3000);

        var todos = (await client.GetFromJsonAsync<PagedDto>("/gastos"))!;
        Assert.Equal(3, todos.Total);
        Assert.Equal(3, todos.Data.Count);

        var soloGas = (await client.GetFromJsonAsync<PagedDto>("/gastos?categoria=GAS"))!;
        Assert.Equal(2, soloGas.Total);
        Assert.All(soloGas.Data, g => Assert.Equal("GAS", g.Categoria));

        // Paginado: limit 1, página 0 (0-indexado) → 1 fila, total 3.
        var pag = (await client.GetFromJsonAsync<PagedDto>("/gastos?page=0&limit=1"))!;
        Assert.Single(pag.Data);
        Assert.Equal(3, pag.Total);
    }

    [Fact]
    public async Task Resumen_agrupa_por_categoria()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        await CrearAsync(client, "LUZ", 1000);
        await CrearAsync(client, "GAS", 2000);
        await CrearAsync(client, "GAS", 3000);

        var resumen = (await client.GetFromJsonAsync<ResumenDto>("/gastos/resumen"))!;
        Assert.Equal(6000, resumen.Total);
        Assert.Equal(3, resumen.Cantidad);

        // Ordenado por total desc → GAS (5000) primero, LUZ (1000) después.
        Assert.Equal("GAS", resumen.PorCategoria[0].Categoria);
        Assert.Equal(5000, resumen.PorCategoria[0].Total);
        Assert.Equal(2, resumen.PorCategoria[0].Cantidad);
        Assert.Equal("LUZ", resumen.PorCategoria[1].Categoria);
    }

    [Fact]
    public async Task Actualizar_y_eliminar()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var id = await CrearAsync(client, "AGUA", 500);

        var upd = await client.PutAsJsonAsync($"/gastos/{id}", new { monto = 750, categoria = "MANTENIMIENTO" });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        var actualizado = (await upd.Content.ReadFromJsonAsync<GastoDto>())!;
        Assert.Equal(750, actualizado.Monto);
        Assert.Equal("MANTENIMIENTO", actualizado.Categoria);

        var del = await client.DeleteAsync($"/gastos/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var luego = await client.GetAsync($"/gastos/{id}");
        Assert.Equal(HttpStatusCode.NotFound, luego.StatusCode);
    }

    [Fact]
    public async Task Gastos_es_solo_admin()
    {
        var trab = await LoginAsync(TrabajadorAEmail, NegocioASlug);
        var resp = await trab.PostAsJsonAsync("/gastos", new { categoria = "LUZ", monto = 100 });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Aislamiento_multitenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var idA = await CrearAsync(clientA, "LUZ", 1000);

        // B no ve el gasto de A ni en el detalle ni en el listado.
        var cross = await clientB.GetAsync($"/gastos/{idA}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        var listaB = (await clientB.GetFromJsonAsync<PagedDto>("/gastos"))!;
        Assert.DoesNotContain(listaB.Data, g => g.Id == idA);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> CrearAsync(HttpClient client, string categoria, double monto)
    {
        var resp = await client.PostAsJsonAsync("/gastos", new { categoria, monto });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<GastoDto>())!.Id;
    }

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private sealed record GastoDto(string Id, string Categoria, double Monto, string? Descripcion, DateTime Fecha);

    private sealed record PagedDto(List<GastoDto> Data, int Total);

    private sealed record ResumenCategoriaDto(string Categoria, double Total, int Cantidad);

    private sealed record ResumenDto(double Total, int Cantidad, List<ResumenCategoriaDto> PorCategoria);
}
