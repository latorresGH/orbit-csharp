using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Extras;

/// <summary>
/// Test de integración end-to-end del <c>ExtraController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre el patrón gemelo de Aderezo más las particularidades de Extra:
/// <list type="bullet">
///   <item>aislamiento multi-tenant + listados públicos por <c>?negocio=slug</c>;</item>
///   <item>caso estrella cross-tenant: precio/consumo con categoría ajena → 404, y crear con
///   <c>categoriaIds</c>/<c>toppingGrupoId</c> ajenos → 400;</item>
///   <item>stock atómico, tanto extra-backed como insumo-backed (el movimiento opera sobre el Insumo
///   asociado y descuenta del lugar correcto).</item>
/// </list>
/// </summary>
[Collection(ExtraApiCollection.Name)]
public sealed class ExtrasIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-ex-a";
    private const string NegocioBId = "neg-ex-b";
    private const string NegocioASlug = "negocio-ex-a";
    private const string NegocioBSlug = "negocio-ex-b";
    private const string AdminAId = "user-ex-a";
    private const string AdminBId = "user-ex-b";
    private const string AdminAEmail = "admin-a@ex.test";
    private const string AdminBEmail = "admin-b@ex.test";
    private const string CategoriaAId = "cat-ex-a";
    private const string CategoriaBId = "cat-ex-b";
    private const string GrupoAId = "tg-ex-a";
    private const string GrupoBId = "tg-ex-b";
    private const string InsumoAId = "ins-ex-a";

    private readonly ExtraWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocio(db, hasher, NegocioAId, "Negocio EX A", NegocioASlug, AdminAId, AdminAEmail, CategoriaAId, GrupoAId, now);
        SeedNegocio(db, hasher, NegocioBId, "Negocio EX B", NegocioBSlug, AdminBId, AdminBEmail, CategoriaBId, GrupoBId, now);

        // Insumo sólo para el negocio A (para el caso insumo-backed). Stock inicial 10.
        db.Insumos.Add(new Insumo
        {
            Id = InsumoAId,
            Nombre = "Queso",
            StockActual = 10,
            StockMinimo = 0,
            Activo = true,
            NegocioId = NegocioAId,
            CreatedAt = now,
            UpdatedAt = now,
        });

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

        // Queso global con stock (para que aparezca en por-categoria-producto, que exige stock > 0).
        var quesoA = await CrearExtraAsync(clientA, new { nombre = "Queso extra", esGlobal = true, stockActual = 10.0 });
        var baconA = await CrearExtraAsync(clientA, new { nombre = "Bacon", esGlobal = false });

        // Defaults de NestJS: precio 500, categoría 'TOPPINGS', y stock 0 cuando no se envía (Bacon).
        Assert.Equal(500, quesoA.Precio);
        Assert.Equal("TOPPINGS", quesoA.Categoria);
        Assert.Equal(0, baconA.StockActual);

        var cheddarB = await CrearExtraAsync(clientB, new { nombre = "Cheddar" });

        // A sólo ve los suyos; B el suyo. Cross-tenant por id → 404.
        var extrasA = await ListarAutenticadoAsync(clientA);
        Assert.Equal(2, extrasA.Length);
        Assert.All(extrasA, e => Assert.NotEqual(cheddarB.Id, e.Id));

        var crossGet = await clientA.GetAsync($"/extras/{cheddarB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);

        // Unicidad por negocio: A no repite "Bacon" (409); B sí puede usar "Bacon".
        var dup = await clientA.PostAsJsonAsync("/extras", new { nombre = "Bacon" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var baconB = await clientB.PostAsJsonAsync("/extras", new { nombre = "Bacon" });
        Assert.Equal(HttpStatusCode.Created, baconB.StatusCode);

        // ── Listado PÚBLICO de A por slug (cliente anónimo) ───────────────────────────────────
        var anon = _factory.CreateClient();
        var publicosA = await GetJsonArrayAsync(anon, $"/extras?negocio={NegocioASlug}");
        Assert.Equal(2, publicosA.Length);
        Assert.Contains(publicosA, e => e.Id == quesoA.Id);
        Assert.DoesNotContain(publicosA, e => e.Id == cheddarB.Id);

        // Sin parámetro 'negocio' → 400; slug inexistente → 404.
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/extras")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/extras?negocio=no-existe")).StatusCode);

        // por-categoria-producto público: sólo global o vinculado (Queso es global, Bacon no).
        var porCat = await GetJsonArrayAsync(anon, $"/extras/por-categoria-producto/{CategoriaAId}?negocio={NegocioASlug}");
        Assert.Single(porCat);
        Assert.Equal(quesoA.Id, porCat[0].Id);
    }

    [Fact]
    public async Task Cross_tenant_en_categoria_grupo_y_precio_consumo()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var extraA = await CrearExtraAsync(clientA, new { nombre = "Huevo" });

        // ── Precio/consumo con categoría PROPIA → 200; con categoría AJENA → 404 (query filter) ──
        Assert.Equal(HttpStatusCode.OK, (await clientA.PostAsJsonAsync("/extras/precio-categoria",
            new { extraId = extraA.Id, categoriaId = CategoriaAId, precio = 80.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.PostAsJsonAsync("/extras/precio-categoria",
            new { extraId = extraA.Id, categoriaId = CategoriaBId, precio = 80.0 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await clientA.PostAsJsonAsync("/extras/consumo-categoria",
            new { extraId = extraA.Id, categoriaId = CategoriaAId, cantidadConsumo = 2.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.PostAsJsonAsync("/extras/consumo-categoria",
            new { extraId = extraA.Id, categoriaId = CategoriaBId, cantidadConsumo = 2.0 })).StatusCode);

        // ── Crear con categoriaIds de OTRO negocio → 400 ─────────────────────────────────────
        Assert.Equal(HttpStatusCode.BadRequest, (await clientA.PostAsJsonAsync("/extras",
            new { nombre = "Cebolla", categoriaIds = new[] { CategoriaBId } })).StatusCode);

        // ── Crear con toppingGrupoId de OTRO negocio → 400 (protección estructural pedida) ───
        Assert.Equal(HttpStatusCode.BadRequest, (await clientA.PostAsJsonAsync("/extras",
            new { nombre = "Lechuga", toppingGrupoId = GrupoBId })).StatusCode);

        // ── Crear con toppingGrupoId PROPIO → 201 y queda vinculado ──────────────────────────
        var conGrupo = await CrearExtraAsync(clientA, new { nombre = "Tomate", toppingGrupoId = GrupoAId });
        Assert.Equal(GrupoAId, conGrupo.ToppingGrupoId);

        // ── GET precio por categoría: específico (80) vs fallback al precio base (500) ────────
        Assert.Equal(80.0, await GetDoubleAsync(clientA, $"/extras/{extraA.Id}/precio/{CategoriaAId}"));
        Assert.Equal(500.0, await GetDoubleAsync(clientA, $"/extras/{extraA.Id}/precio/cat-inexistente"));

        // El detalle trae el precio y consumo de la categoría propia.
        var detalle = await GetExtraAsync(clientA, extraA.Id);
        Assert.Single(detalle.PreciosPorCategoria);
        Assert.Equal(80.0, detalle.PreciosPorCategoria[0].Precio);
        Assert.Single(detalle.ConsumosPorCategoria);
        Assert.Equal(2.0, detalle.ConsumosPorCategoria[0].CantidadConsumo);
    }

    [Fact]
    public async Task Stock_extra_backed_y_insumo_backed()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // ── Extra-backed: opera sobre extra.stockActual ──────────────────────────────────────
        var simple = await CrearExtraAsync(clientA, new { nombre = "Pepinillos", stockActual = 5.0 });
        Assert.Equal(5, simple.StockActual);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await clientA.PatchAsJsonAsync($"/extras/{simple.Id}/descontar", new { cantidad = 100.0 })).StatusCode);
        Assert.Equal(2, (await PatchExtraAsync(clientA, $"/extras/{simple.Id}/descontar", new { cantidad = 3.0 })).StockActual);
        Assert.Equal(10, (await PatchExtraAsync(clientA, $"/extras/{simple.Id}/sumar", new { cantidad = 8.0 })).StockActual);

        // ── Insumo-backed: el extra tiene insumoId → el movimiento opera sobre el Insumo ─────
        var conInsumo = await CrearExtraAsync(clientA, new { nombre = "Muzzarella", insumoId = InsumoAId });
        Assert.Equal(InsumoAId, conInsumo.InsumoId);

        // Descontar 4 del extra insumo-backed: el stock del EXTRA no cambia, baja el del INSUMO (10 → 6).
        var trasDescuento = await PatchExtraAsync(clientA, $"/extras/{conInsumo.Id}/descontar", new { cantidad = 4.0 });
        Assert.Equal(0, trasDescuento.StockActual); // el extra sigue en su stock propio (0)

        // Descontar más que el stock del insumo (6) → 400 con el mensaje del insumo.
        var insuficiente = await clientA.PatchAsJsonAsync($"/extras/{conInsumo.Id}/descontar", new { cantidad = 100.0 });
        Assert.Equal(HttpStatusCode.BadRequest, insuficiente.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        // El insumo quedó en 6 (10 - 4); el descuento fallido de 100 no lo tocó.
        var insumo = await db.Insumos.IgnoreQueryFilters().FirstAsync(i => i.Id == InsumoAId);
        Assert.Equal(6, insumo.StockActual);

        // Movimientos del extra simple: 2 (descuento -3 + suma +8), con extraId y UserId del admin.
        var movsSimple = await db.StockMovimientos.IgnoreQueryFilters()
            .Where(m => m.ExtraId == simple.Id).OrderBy(m => m.Cantidad).ToListAsync();
        Assert.Equal(2, movsSimple.Count);
        Assert.All(movsSimple, m => Assert.Equal("AJUSTE_MANUAL", m.Tipo));
        Assert.All(movsSimple, m => Assert.Equal(AdminAId, m.UserId));
        Assert.Equal(-3, movsSimple[0].Cantidad);
        Assert.Equal(8, movsSimple[1].Cantidad);

        // El movimiento insumo-backed se registró con insumoId (no extraId).
        var movInsumo = await db.StockMovimientos.IgnoreQueryFilters()
            .Where(m => m.InsumoId == InsumoAId).ToListAsync();
        Assert.Single(movInsumo);
        Assert.Equal(-4, movInsumo[0].Cantidad);
        Assert.Equal(10, movInsumo[0].StockAntes);
        Assert.Equal(6, movInsumo[0].StockDespues);
        Assert.Null(movInsumo[0].ExtraId);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ExtraDto> CrearExtraAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/extras", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadExtraAsync(response);
    }

    private static async Task<ExtraDto> GetExtraAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync($"/extras/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadExtraAsync(response);
    }

    private static async Task<ExtraDto> PatchExtraAsync(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadExtraAsync(response);
    }

    private static async Task<ExtraDto[]> ListarAutenticadoAsync(HttpClient client) =>
        await GetJsonArrayAsync(client, "/extras");

    private static async Task<ExtraDto[]> GetJsonArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<ExtraDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
    }

    private static async Task<double> GetDoubleAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<double>();
    }

    private static async Task<ExtraDto> ReadExtraAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<ExtraDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocio(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, string categoriaId, string grupoId, DateTime now)
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
        db.ToppingGrupos.Add(new ToppingGrupo
        {
            Id = grupoId,
            Nombre = "Salsas",
            MaxExtrasGratis = 3,
            EsIncluido = true,
            Orden = 0,
            Activo = true,
            NegocioId = negocioId,
        });
    }

    private sealed record ExtraDto(
        string Id,
        string Nombre,
        double Precio,
        double StockActual,
        bool Activo,
        string Categoria,
        bool EsGlobal,
        bool EsPremium,
        string? InsumoId,
        string? ToppingGrupoId,
        List<ExtraCategoriaDto> CategoriasAplica,
        List<ExtraPrecioDto> PreciosPorCategoria,
        List<ExtraConsumoDto> ConsumosPorCategoria);

    private sealed record ExtraCategoriaDto(string Id, string CategoriaId, string? CategoriaNombre);

    private sealed record ExtraPrecioDto(string Id, string CategoriaId, string? CategoriaNombre, double Precio);

    private sealed record ExtraConsumoDto(string Id, string CategoriaId, string? CategoriaNombre, double CantidadConsumo);
}
