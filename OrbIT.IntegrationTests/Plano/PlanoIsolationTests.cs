using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Plano;

/// <summary>
/// Test de integración end-to-end del <c>PlanoController</c> real (in-memory) contra una base PostgreSQL
/// dedicada. Cubre:
/// <list type="bullet">
///   <item>GET sin plano → 404; upsert (PUT) crea; GET lo devuelve; el jsonb de elementos round-trippea;</item>
///   <item>aislamiento multi-tenant (B no ve el plano de A, cada negocio tiene el suyo 1:1);</item>
///   <item>PUT idempotente (segundo upsert reemplaza en la misma fila, no crea otra) y DELETE = reset;</item>
///   <item>gate de plan: negocio Básico (sin <c>tieneMesas</c>) → 403 en todos los endpoints;</item>
///   <item>validación de tipo/forma inválidos → 400.</item>
/// </list>
/// </summary>
[Collection(PlanoApiCollection.Name)]
public sealed class PlanoIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-pl-a";
    private const string NegocioBId = "neg-pl-b";
    private const string NegocioBasicId = "neg-pl-basic";
    private const string NegocioASlug = "negocio-pl-a";
    private const string NegocioBSlug = "negocio-pl-b";
    private const string NegocioBasicSlug = "negocio-pl-basic";
    private const string AdminAEmail = "admin-pl-a@pl.test";
    private const string AdminBEmail = "admin-pl-b@pl.test";
    private const string AdminBasicEmail = "admin-pl-basic@pl.test";

    private readonly PlanoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        PlanSeed.Seed(db, now);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio PL A", NegocioASlug, "user-pl-a", AdminAEmail, PlanSeed.ProId, "pro", now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio PL B", NegocioBSlug, "user-pl-b", AdminBEmail, PlanSeed.ProId, "pro", now);
        SeedNegocioConAdmin(db, hasher, NegocioBasicId, "Negocio PL Basic", NegocioBasicSlug, "user-pl-basic", AdminBasicEmail, PlanSeed.BasicId, "basic", now);
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
    public async Task Get_sin_plano_devuelve_404_y_upsert_crea_y_persiste_elementos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // Sin plano todavía → 404.
        var vacio = await clientA.GetAsync("/plano");
        Assert.Equal(HttpStatusCode.NotFound, vacio.StatusCode);

        // Upsert crea el plano con un elemento mesa + una pared.
        var body = new
        {
            canvasWidth = 1000,
            canvasHeight = 700,
            elementos = new object[]
            {
                new { id = "el-1", tipo = "mesa", x = 100, y = 150, width = 80, height = 80, forma = "cuadrada", mesaId = (string?)null, etiqueta = (string?)null, capacidad = 4, rotation = 0 },
                new { id = "el-2", tipo = "pared", x = 0, y = 0, width = 120, height = 20, forma = "rectangular", mesaId = (string?)null, etiqueta = (string?)null, capacidad = (int?)null, rotation = 90 },
            },
        };
        var put = await clientA.PutAsJsonAsync("/plano", body);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var creado = (await put.Content.ReadFromJsonAsync<PlanoDto>())!;
        Assert.Equal(1000, creado.CanvasWidth);
        Assert.Equal(700, creado.CanvasHeight);
        Assert.Equal(2, creado.Elementos.Count);

        // GET devuelve el mismo plano con el jsonb round-trippeado (incluye mesaId null, capacidad, rotation).
        var plano = await clientA.GetFromJsonAsync<PlanoDto>("/plano");
        Assert.NotNull(plano);
        var mesa = Assert.Single(plano!.Elementos, e => e.Id == "el-1");
        Assert.Equal("mesa", mesa.Tipo);
        Assert.Equal("cuadrada", mesa.Forma);
        Assert.Equal(4, mesa.Capacidad);
        Assert.Null(mesa.MesaId);
        var pared = Assert.Single(plano.Elementos, e => e.Id == "el-2");
        Assert.Equal("pared", pared.Tipo);
        Assert.Equal(90, pared.Rotation);
        Assert.Null(pared.Capacidad);
    }

    [Fact]
    public async Task Aislamiento_multitenant_cada_negocio_tiene_su_propio_plano()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        await UpsertAsync(clientA, "el-a", "mesa", "cuadrada");
        await UpsertAsync(clientB, "el-b", "barra", "rectangular");

        // A sólo ve su elemento; B sólo el suyo.
        var planoA = await clientA.GetFromJsonAsync<PlanoDto>("/plano");
        var elA = Assert.Single(planoA!.Elementos);
        Assert.Equal("el-a", elA.Id);

        var planoB = await clientB.GetFromJsonAsync<PlanoDto>("/plano");
        var elB = Assert.Single(planoB!.Elementos);
        Assert.Equal("el-b", elB.Id);

        Assert.NotEqual(planoA.Id, planoB.Id);
        Assert.Equal(NegocioAId, planoA.NegocioId);
        Assert.Equal(NegocioBId, planoB.NegocioId);
    }

    [Fact]
    public async Task Upsert_es_idempotente_y_reemplaza_en_la_misma_fila()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var primero = await UpsertAsync(clientA, "el-1", "mesa", "cuadrada");
        var segundo = await UpsertAsync(clientA, "el-2", "pared", "rectangular");

        // Mismo id de plano (reemplazo, no fila nueva); el layout es el del segundo upsert.
        Assert.Equal(primero.Id, segundo.Id);
        var plano = await clientA.GetFromJsonAsync<PlanoDto>("/plano");
        var el = Assert.Single(plano!.Elementos);
        Assert.Equal("el-2", el.Id);
    }

    [Fact]
    public async Task Delete_resetea_los_elementos_conservando_la_fila()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        await UpsertAsync(clientA, "el-1", "mesa", "cuadrada");

        var reset = await clientA.DeleteAsync("/plano");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var reseteado = (await reset.Content.ReadFromJsonAsync<PlanoDto>())!;
        Assert.Empty(reseteado.Elementos);

        // La fila sigue existiendo (GET no da 404), con elementos vacíos.
        var plano = await clientA.GetFromJsonAsync<PlanoDto>("/plano");
        Assert.NotNull(plano);
        Assert.Empty(plano!.Elementos);
    }

    [Fact]
    public async Task Delete_sin_plano_devuelve_404()
    {
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);
        var reset = await clientB.DeleteAsync("/plano");
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
    }

    [Fact]
    public async Task Plan_basico_sin_tieneMesas_devuelve_403_en_todos_los_endpoints()
    {
        var client = await LoginAsync(AdminBasicEmail, NegocioBasicSlug);

        var get = await client.GetAsync("/plano");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);

        var put = await client.PutAsJsonAsync("/plano", new { canvasWidth = 800, canvasHeight = 600, elementos = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var del = await client.DeleteAsync("/plano");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task Upsert_con_tipo_o_forma_invalidos_devuelve_400()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var tipoMalo = await clientA.PutAsJsonAsync("/plano", new
        {
            canvasWidth = 800,
            canvasHeight = 600,
            elementos = new object[] { new { id = "x", tipo = "silla", x = 0, y = 0, width = 10, height = 10, forma = "cuadrada", rotation = 0 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, tipoMalo.StatusCode);

        var formaMala = await clientA.PutAsJsonAsync("/plano", new
        {
            canvasWidth = 800,
            canvasHeight = 600,
            elementos = new object[] { new { id = "x", tipo = "mesa", x = 0, y = 0, width = 10, height = 10, forma = "triangular", rotation = 0 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, formaMala.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<PlanoDto> UpsertAsync(HttpClient client, string elementoId, string tipo, string forma)
    {
        var body = new
        {
            canvasWidth = 1200,
            canvasHeight = 800,
            elementos = new object[]
            {
                new { id = elementoId, tipo, x = 50, y = 60, width = 80, height = 80, forma, rotation = 0 },
            },
        };
        var response = await client.PutAsJsonAsync("/plano", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PlanoDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocioConAdmin(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, string planId, string planNombre, DateTime now)
    {
        db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = negocioNombre,
            Slug = slug,
            Activo = true,
            Plan = planNombre,
            PlanId = planId,
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

    private sealed record PlanoDto(
        string Id,
        string NegocioId,
        List<ElementoDto> Elementos,
        int CanvasWidth,
        int CanvasHeight);

    private sealed record ElementoDto(
        string Id,
        string Tipo,
        int X,
        int Y,
        int Width,
        int Height,
        string Forma,
        string? MesaId,
        string? Etiqueta,
        int? Capacidad,
        int Rotation);
}
