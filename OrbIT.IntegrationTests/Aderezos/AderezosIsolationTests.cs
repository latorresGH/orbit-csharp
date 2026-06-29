using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Aderezos;

/// <summary>
/// Test de integración end-to-end del <c>AderezoController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant (auth por cookie → claim negocioId → query filter);</item>
///   <item>los listados públicos por <c>?negocio=slug</c> (mecanismo <c>[AllowAnonymousWithTenant]</c>):
///   slug válido, faltante (400) e inexistente (404);</item>
///   <item>el caso estrella de la auditoría: asignar precio/consumo usando una categoría de OTRO
///   negocio devuelve 404 gracias al query filter (sin chequeo manual de tenant);</item>
///   <item>el descuento de stock atómico (insuficiente → 400) y el registro de StockMovimiento.</item>
/// </list>
/// </summary>
[Collection(AderezoApiCollection.Name)]
public sealed class AderezosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-ad-a";
    private const string NegocioBId = "neg-ad-b";
    private const string NegocioASlug = "negocio-ad-a";
    private const string NegocioBSlug = "negocio-ad-b";
    private const string AdminAId = "user-ad-a";
    private const string AdminBId = "user-ad-b";
    private const string AdminAEmail = "admin-a@ad.test";
    private const string AdminBEmail = "admin-b@ad.test";
    private const string CategoriaAId = "cat-ad-a";
    private const string CategoriaBId = "cat-ad-b";

    private readonly AderezoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdminYCategoria(db, hasher, NegocioAId, "Negocio AD A", NegocioASlug, AdminAId, AdminAEmail, CategoriaAId, now);
        SeedNegocioConAdminYCategoria(db, hasher, NegocioBId, "Negocio AD B", NegocioBSlug, AdminBId, AdminBEmail, CategoriaBId, now);
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
    public async Task Aislamiento_multitenant_y_listados_publicos_end_to_end()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        // ── A crea dos aderezos: Ketchup global, Mostaza no-global ───────────────────────────
        var ketchupA = await CrearAderezoAsync(clientA, new { nombre = "Ketchup", esGlobal = true });
        var mostazaA = await CrearAderezoAsync(clientA, new { nombre = "Mostaza", esGlobal = false });

        // Defaults de NestJS replicados: stock 999, precio 0, no premium.
        Assert.Equal(999, ketchupA.StockActual);
        Assert.Equal(0, ketchupA.Precio);
        Assert.False(ketchupA.EsPremium);

        // ── B crea un aderezo con un nombre que A también podría usar ─────────────────────────
        var mayonesaB = await CrearAderezoAsync(clientB, new { nombre = "Mayonesa" });

        // ── A sólo ve SUS aderezos, nunca el de B ─────────────────────────────────────────────
        var aderezosA = await ListarAutenticadoAsync(clientA);
        Assert.Equal(2, aderezosA.Length);
        Assert.All(aderezosA, a => Assert.NotEqual(mayonesaB.Id, a.Id));

        // ── A no puede acceder al aderezo de B por id (404, no 403) ───────────────────────────
        var crossTenantGet = await clientA.GetAsync($"/aderezos/{mayonesaB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantGet.StatusCode);

        // ── Unicidad de nombre por negocio: A no puede repetir "Ketchup" → 409 ────────────────
        var duplicado = await clientA.PostAsJsonAsync("/aderezos", new { nombre = "Ketchup" });
        Assert.Equal(HttpStatusCode.Conflict, duplicado.StatusCode);

        // ── B sí puede usar "Ketchup" (otro negocio, otra unicidad) ──────────────────────────
        var ketchupB = await clientB.PostAsJsonAsync("/aderezos", new { nombre = "Ketchup" });
        Assert.Equal(HttpStatusCode.Created, ketchupB.StatusCode);

        // ── Listado PÚBLICO de A por slug (cliente anónimo, sin login) ───────────────────────
        var anon = _factory.CreateClient();
        var publicosA = await GetJsonArrayAsync(anon, $"/aderezos?negocio={NegocioASlug}");
        Assert.Equal(2, publicosA.Length);
        Assert.Contains(publicosA, a => a.Id == ketchupA.Id);
        Assert.Contains(publicosA, a => a.Id == mostazaA.Id);
        Assert.DoesNotContain(publicosA, a => a.Id == mayonesaB.Id);

        // ── Público sin parámetro 'negocio' → 400 ────────────────────────────────────────────
        var sinNegocio = await anon.GetAsync("/aderezos");
        Assert.Equal(HttpStatusCode.BadRequest, sinNegocio.StatusCode);

        // ── Público con slug inexistente → 404 ───────────────────────────────────────────────
        var slugMalo = await anon.GetAsync("/aderezos?negocio=no-existe");
        Assert.Equal(HttpStatusCode.NotFound, slugMalo.StatusCode);

        // ── por-categoria-producto público: sólo global o vinculado a la categoría ───────────
        // Ketchup es global → aparece; Mostaza no-global y sin vínculo → no aparece.
        var porCategoria = await GetJsonArrayAsync(anon, $"/aderezos/por-categoria-producto/{CategoriaAId}?negocio={NegocioASlug}");
        Assert.Single(porCategoria);
        Assert.Equal(ketchupA.Id, porCategoria[0].Id);
    }

    [Fact]
    public async Task Precio_y_consumo_con_categoria_de_otro_tenant_dan_404()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var aderezoA = await CrearAderezoAsync(clientA, new { nombre = "Alioli" });

        // ── Precio con la categoría PROPIA → 200 ─────────────────────────────────────────────
        var precioOk = await clientA.PostAsJsonAsync("/aderezos/precio-categoria",
            new { aderezoId = aderezoA.Id, categoriaId = CategoriaAId, precio = 50.0 });
        Assert.Equal(HttpStatusCode.OK, precioOk.StatusCode);

        // ── Precio con la categoría de OTRO negocio → 404 (query filter, sin chequeo manual) ──
        var precioCross = await clientA.PostAsJsonAsync("/aderezos/precio-categoria",
            new { aderezoId = aderezoA.Id, categoriaId = CategoriaBId, precio = 50.0 });
        Assert.Equal(HttpStatusCode.NotFound, precioCross.StatusCode);

        // ── Consumo con la categoría PROPIA → 200 ────────────────────────────────────────────
        var consumoOk = await clientA.PostAsJsonAsync("/aderezos/consumo-categoria",
            new { aderezoId = aderezoA.Id, categoriaId = CategoriaAId, cantidadConsumo = 1.5 });
        Assert.Equal(HttpStatusCode.OK, consumoOk.StatusCode);

        // ── Consumo con la categoría de OTRO negocio → 404 ───────────────────────────────────
        var consumoCross = await clientA.PostAsJsonAsync("/aderezos/consumo-categoria",
            new { aderezoId = aderezoA.Id, categoriaId = CategoriaBId, cantidadConsumo = 1.5 });
        Assert.Equal(HttpStatusCode.NotFound, consumoCross.StatusCode);

        // ── Crear categoriaIds cross-tenant también se rechaza (400) ─────────────────────────
        var createCross = await clientA.PostAsJsonAsync("/aderezos",
            new { nombre = "Pesto", categoriaIds = new[] { CategoriaBId } });
        Assert.Equal(HttpStatusCode.BadRequest, createCross.StatusCode);

        // ── El detalle del aderezo trae el precio y el consumo de la categoría propia ─────────
        var detalle = await GetAderezoAsync(clientA, aderezoA.Id);
        Assert.Single(detalle.PreciosPorCategoria);
        Assert.Equal(50.0, detalle.PreciosPorCategoria[0].Precio);
        Assert.Equal(CategoriaAId, detalle.PreciosPorCategoria[0].CategoriaId);
        Assert.Single(detalle.ConsumosPorCategoria);
        Assert.Equal(1.5, detalle.ConsumosPorCategoria[0].CantidadConsumo);

        // ── GET consumo por categoría: propia devuelve el valor; ajena devuelve 0 ────────────
        var consumoLeido = await GetDoubleAsync(clientA, $"/aderezos/{aderezoA.Id}/consumo/{CategoriaAId}");
        Assert.Equal(1.5, consumoLeido);
        var consumoAjeno = await GetDoubleAsync(clientA, $"/aderezos/{aderezoA.Id}/consumo/{CategoriaBId}");
        Assert.Equal(0, consumoAjeno);
    }

    [Fact]
    public async Task Descontar_por_debajo_del_stock_falla_y_registra_movimientos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var stocky = await CrearAderezoAsync(clientA, new { nombre = "Stocky", stockActual = 5.0 });
        Assert.Equal(5, stocky.StockActual);

        // ── Descontar más que el stock → 400 "Stock insuficiente" (sin tocar el stock) ───────
        var insuficiente = await clientA.PatchAsJsonAsync($"/aderezos/{stocky.Id}/descontar", new { cantidad = 100.0 });
        Assert.Equal(HttpStatusCode.BadRequest, insuficiente.StatusCode);

        // ── Descontar 3 (5 → 2) ──────────────────────────────────────────────────────────────
        var descontado = await PatchAderezoAsync(clientA, $"/aderezos/{stocky.Id}/descontar", new { cantidad = 3.0 });
        Assert.Equal(2, descontado.StockActual);

        // ── Sumar 8 (2 → 10) ─────────────────────────────────────────────────────────────────
        var sumado = await PatchAderezoAsync(clientA, $"/aderezos/{stocky.Id}/sumar", new { cantidad = 8.0 });
        Assert.Equal(10, sumado.StockActual);

        // ── Se registraron exactamente 2 StockMovimiento (descuento + suma), con UserId del admin ──
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var movimientos = await db.StockMovimientos.IgnoreQueryFilters()
            .Where(m => m.AderezoId == stocky.Id)
            .OrderBy(m => m.Cantidad)
            .ToListAsync();

        Assert.Equal(2, movimientos.Count);
        Assert.All(movimientos, m => Assert.Equal("AJUSTE_MANUAL", m.Tipo));
        Assert.All(movimientos, m => Assert.Equal(NegocioAId, m.NegocioId));
        Assert.All(movimientos, m => Assert.Equal(AdminAId, m.UserId));

        // El de menor 'cantidad' es el descuento (-3): 5 → 2.
        Assert.Equal(-3, movimientos[0].Cantidad);
        Assert.Equal(5, movimientos[0].StockAntes);
        Assert.Equal(2, movimientos[0].StockDespues);
        // El otro es la suma (+8): 2 → 10.
        Assert.Equal(8, movimientos[1].Cantidad);
        Assert.Equal(2, movimientos[1].StockAntes);
        Assert.Equal(10, movimientos[1].StockDespues);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<AderezoDto> CrearAderezoAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/aderezos", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAderezoAsync(response);
    }

    private static async Task<AderezoDto> GetAderezoAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/aderezos/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAderezoAsync(response);
    }

    private static async Task<AderezoDto> PatchAderezoAsync(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAderezoAsync(response);
    }

    private static async Task<AderezoDto[]> ListarAutenticadoAsync(HttpClient client) =>
        await GetJsonArrayAsync(client, "/aderezos");

    private static async Task<AderezoDto[]> GetJsonArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<AderezoDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static async Task<double> GetDoubleAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<double>();
    }

    private static async Task<AderezoDto> ReadAderezoAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<AderezoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocioConAdminYCategoria(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, string categoriaId, DateTime now)
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
        db.Categoria.Add(new Categorium
        {
            Id = categoriaId,
            Nombre = "Pizzas",
            Activo = true,
            Orden = 0,
            MaxAderezosGratis = 2,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private sealed record AderezoDto(
        string Id,
        string Nombre,
        bool Activo,
        double StockActual,
        bool EsGlobal,
        bool EsPremium,
        double Precio,
        List<AderezoCategoriaDto> CategoriasAplica,
        List<AderezoPrecioDto> PreciosPorCategoria,
        List<AderezoConsumoDto> ConsumosPorCategoria);

    private sealed record AderezoCategoriaDto(string Id, string CategoriaId, string? CategoriaNombre);

    private sealed record AderezoPrecioDto(string Id, string CategoriaId, string? CategoriaNombre, double Precio);

    private sealed record AderezoConsumoDto(string Id, string CategoriaId, string? CategoriaNombre, double CantidadConsumo);
}
