using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

using System.Text.Json.Serialization;

namespace OrbIT.IntegrationTests.Insumos;

/// <summary>
/// Tests end-to-end de los 3 endpoints de la Tanda B del <c>InsumoController</c> (los que dependían de Turnos):
/// <list type="bullet">
///   <item><c>POST /insumos/disponibilidad</c> — stock/consumo por categoría, público por slug y aislado;</item>
///   <item><c>GET /insumos/reporte/consumo</c> — GroupBy de <c>DESCUENTO_PEDIDO</c> por insumo, con filtro AR;</item>
///   <item><c>GET /insumos/movimientos</c> — fusión de stock + eventos de turno (apertura/cierre) y filtros.</item>
/// </list>
/// Comparte la misma colección (serializada) y base dedicada que <see cref="InsumosIsolationTests"/>.
/// </summary>
[Collection(InsumoApiCollection.Name)]
public sealed class InsumosTandaBTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-tb-a";
    private const string NegocioBId = "neg-tb-b";
    private const string NegocioASlug = "negocio-tb-a";
    private const string NegocioBSlug = "negocio-tb-b";
    private const string AdminAId = "user-tb-a";
    private const string AdminBId = "user-tb-b";
    private const string AdminAEmail = "admin-a@tb.test";
    private const string AdminBEmail = "admin-b@tb.test";

    private const string CategoriaId = "cat-tb-1";
    private const string InsumoQuesoId = "ins-tb-queso";   // respalda al extra "exQueso", stock 20
    private const string InsumoMuzzaId = "ins-tb-muzza";   // reporte de consumo
    private const string InsumoTomateId = "ins-tb-tomate"; // reporte de consumo
    private const string ExtraQuesoId = "extra-tb-queso";  // backed by insumo (stock 20), consumo cat=4
    private const string ExtraPropioId = "extra-tb-propio"; // stock propio 7, sin consumo config
    private const string ExtraBId = "extra-tb-b";          // de negocio B (aislamiento)
    private const string AderezoKetchupId = "ader-tb-ketchup"; // stock 10, consumo cat=2
    private const string TurnoAId = "turno-tb-a";
    private const string TurnoBId = "turno-tb-b";

    // Fechas de referencia (UTC, Unspecified para columnas timestamp without tz).
    private static readonly DateTime EnRango = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FueraDeRango = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Unspecified);

    private readonly InsumoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocio(db, NegocioAId, "Negocio TB A", NegocioASlug, now);
        SeedNegocio(db, NegocioBId, "Negocio TB B", NegocioBSlug, now);
        SeedUser(db, hasher, AdminAId, AdminAEmail, NegocioAId, now);
        SeedUser(db, hasher, AdminBId, AdminBEmail, NegocioBId, now);

        // Categoría (FK de los consumos por categoría).
        db.Categoria.Add(new Categorium
        {
            Id = CategoriaId, Nombre = "Pizzas", Activo = true, Orden = 0, MaxAderezosGratis = 2,
            NegocioId = NegocioAId, CreatedAt = now, UpdatedAt = now,
        });

        // ── Disponibilidad ────────────────────────────────────────────────────
        db.Insumos.Add(NuevoInsumo(InsumoQuesoId, "Queso insumo", 20, NegocioAId, now));
        db.Extras.Add(NuevoExtra(ExtraQuesoId, "Queso extra", stockActual: 0, insumoId: InsumoQuesoId, NegocioAId, now));
        db.Extras.Add(NuevoExtra(ExtraPropioId, "Extra propio", stockActual: 7, insumoId: null, NegocioAId, now));
        db.Extras.Add(NuevoExtra(ExtraBId, "Extra de B", stockActual: 99, insumoId: null, NegocioBId, now));
        db.Aderezos.Add(NuevoAderezo(AderezoKetchupId, "Ketchup", stockActual: 10, NegocioAId));
        db.ExtraConsumos.Add(new ExtraConsumo { Id = Guid.NewGuid().ToString(), ExtraId = ExtraQuesoId, CategoriaId = CategoriaId, CantidadConsumo = 4, NegocioId = NegocioAId });
        db.AderezoConsumos.Add(new AderezoConsumo { Id = Guid.NewGuid().ToString(), AderezoId = AderezoKetchupId, CategoriaId = CategoriaId, CantidadConsumo = 2, NegocioId = NegocioAId });

        // ── Reporte de consumo ─────────────────────────────────────────────────
        db.Insumos.Add(NuevoInsumo(InsumoMuzzaId, "Muzzarella", 100, NegocioAId, now, UnidadMedida.KILOGRAMO));
        db.Insumos.Add(NuevoInsumo(InsumoTomateId, "Tomate", 100, NegocioAId, now, UnidadMedida.UNIDAD));
        // Muzza: 2 movimientos en rango (-2, -3) → total 5, count 2.
        db.StockMovimientos.Add(Descuento(InsumoMuzzaId, -2, NegocioAId, EnRango));
        db.StockMovimientos.Add(Descuento(InsumoMuzzaId, -3, NegocioAId, EnRango));
        // Tomate: 1 movimiento en rango (-1) → total 1, count 1.
        db.StockMovimientos.Add(Descuento(InsumoTomateId, -1, NegocioAId, EnRango));
        // Excluidos: AJUSTE_MANUAL (otro tipo), DESCUENTO fuera de rango, DESCUENTO de negocio B.
        db.StockMovimientos.Add(new StockMovimiento { Id = Guid.NewGuid().ToString(), InsumoId = InsumoMuzzaId, Tipo = "AJUSTE_MANUAL", Cantidad = -10, StockAntes = 0, StockDespues = 0, NegocioId = NegocioAId, CreatedAt = EnRango });
        db.StockMovimientos.Add(Descuento(InsumoMuzzaId, -99, NegocioAId, FueraDeRango));

        // ── Movimientos unificados: turno cerrado en A y otro en B ─────────────
        db.Turnos.Add(TurnoCerrado(TurnoAId, AdminAId, NegocioAId, apertura: 1000, cierre: 1500, ventas: 400));
        db.Turnos.Add(TurnoCerrado(TurnoBId, AdminBId, NegocioBId, apertura: 500, cierre: 500, ventas: 0));

        await db.SaveChangesAsync();

        // Movimiento de descuento de negocio B (para aislamiento del reporte): con su propio insumo.
        db.Insumos.Add(NuevoInsumo("ins-tb-b", "Insumo B", 10, NegocioBId, now));
        db.StockMovimientos.Add(Descuento("ins-tb-b", -7, NegocioBId, EnRango));
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

    // ═══════════════════════════ Disponibilidad ═══════════════════════════════

    [Fact]
    public async Task Disponibilidad_con_categoria_usa_stock_y_consumo()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var items = await PostDisponibilidadAsync(clientA, new
        {
            extraIds = new[] { ExtraQuesoId, ExtraPropioId },
            aderezoIds = new[] { AderezoKetchupId },
            categoriaId = CategoriaId,
        });

        // Extra respaldado por insumo: stock del insumo (20), consumo 4 → 5.
        var queso = items.Single(i => i.Id == ExtraQuesoId);
        Assert.Equal("EXTRA", queso.Tipo);
        Assert.Equal(20, queso.StockActual);
        Assert.Equal(4, queso.ConsumoPorUnidad);
        Assert.Equal(5, queso.Disponible);

        // Extra sin consumo configurado para la categoría → sinConfig → disponible 0.
        var propio = items.Single(i => i.Id == ExtraPropioId);
        Assert.Equal(7, propio.StockActual);
        Assert.Equal(0, propio.Disponible);

        // Aderezo: stock propio 10, consumo 2 → 5.
        var ketchup = items.Single(i => i.Id == AderezoKetchupId);
        Assert.Equal("ADEREZO", ketchup.Tipo);
        Assert.Equal(10, ketchup.StockActual);
        Assert.Equal(5, ketchup.Disponible);
    }

    [Fact]
    public async Task Disponibilidad_sin_categoria_usa_consumo_default_1()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var items = await PostDisponibilidadAsync(clientA, new
        {
            extraIds = new[] { ExtraQuesoId, ExtraPropioId },
            aderezoIds = Array.Empty<string>(),
        });

        Assert.Equal(20, items.Single(i => i.Id == ExtraQuesoId).Disponible); // floor(20/1)
        Assert.Equal(7, items.Single(i => i.Id == ExtraPropioId).Disponible);  // floor(7/1)
        Assert.All(items, i => Assert.Equal(1, i.ConsumoPorUnidad));
    }

    [Fact]
    public async Task Disponibilidad_publica_por_slug_y_aislada_por_tenant()
    {
        var anon = _factory.CreateClient();

        // Sin sesión ni slug → 400 (ResolveTenantBySlugFilter).
        var sinTenant = await anon.PostAsJsonAsync("/insumos/disponibilidad", new { extraIds = new[] { ExtraQuesoId }, aderezoIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, sinTenant.StatusCode);

        // Público por ?negocio=slug: se ve el extra de A; el de B queda fuera (Global Query Filter).
        var response = await anon.PostAsJsonAsync(
            $"/insumos/disponibilidad?negocio={NegocioASlug}",
            new { extraIds = new[] { ExtraQuesoId, ExtraBId }, aderezoIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<DisponibilidadDto[]>();
        Assert.NotNull(items);
        Assert.Equal(ExtraQuesoId, Assert.Single(items!).Id);
    }

    // ═══════════════════════════ Reporte de consumo ═══════════════════════════

    [Fact]
    public async Task ReporteConsumo_agrupa_suma_abs_y_ordena_desc()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var response = await clientA.GetAsync("/insumos/reporte/consumo?desde=2026-06-01&hasta=2026-06-30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reporte = await response.Content.ReadFromJsonAsync<ReporteConsumoDto[]>();
        Assert.NotNull(reporte);

        // Solo insumos de A con DESCUENTO_PEDIDO en rango: Muzza (5, 2 mov) y Tomate (1, 1 mov), orden desc.
        Assert.Equal(2, reporte!.Length);
        Assert.Equal(InsumoMuzzaId, reporte[0].InsumoId);
        Assert.Equal(5, reporte[0].TotalConsumido);
        Assert.Equal(2, reporte[0].CantidadMovimientos);
        Assert.Equal(InsumoTomateId, reporte[1].InsumoId);
        Assert.Equal(1, reporte[1].TotalConsumido);
        Assert.Equal(1, reporte[1].CantidadMovimientos);
    }

    [Fact]
    public async Task ReporteConsumo_fuera_de_rango_es_vacio()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var response = await clientA.GetAsync("/insumos/reporte/consumo?desde=2027-01-01&hasta=2027-01-31");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reporte = await response.Content.ReadFromJsonAsync<ReporteConsumoDto[]>();
        Assert.NotNull(reporte);
        Assert.Empty(reporte!);
    }

    // ═══════════════════════════ Movimientos unificados ═══════════════════════

    [Fact]
    public async Task Movimientos_unificados_fusiona_stock_y_eventos_de_turno()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var pagina = await GetMovimientosAsync(clientA, "/insumos/movimientos?limit=200");

        // Evento de apertura del turno de A.
        var apertura = pagina.Data.Single(m => m.Id == $"{TurnoAId}_apertura");
        Assert.Equal("APERTURA_TURNO", apertura.Tipo);
        Assert.Equal(1000, apertura.MontoReal);
        Assert.Null(apertura.MontoEsperado);
        Assert.Null(apertura.Diferencia);

        // Evento de cierre: montoEsperado = apertura(1000) + ventas(400) = 1400; diferencia = 1500-1400 = 100.
        var cierre = pagina.Data.Single(m => m.Id == $"{TurnoAId}_cierre");
        Assert.Equal("CIERRE_TURNO", cierre.Tipo);
        Assert.Equal(1500, cierre.MontoReal);
        Assert.Equal(1400, cierre.MontoEsperado);
        Assert.Equal(100, cierre.Diferencia);

        // Las filas de stock traen montoReal null y no exponen confirmadoPor.
        var filaStock = pagina.Data.First(m => m.Tipo == "DESCUENTO_PEDIDO");
        Assert.Null(filaStock.MontoReal);
        Assert.Null(filaStock.ConfirmadoPor);

        // Aislamiento: ningún evento del turno de B.
        Assert.DoesNotContain(pagina.Data, m => m.Id.StartsWith(TurnoBId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Movimientos_filtro_tipo_cierre_solo_devuelve_cierres()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var pagina = await GetMovimientosAsync(clientA, "/insumos/movimientos?tipo=CIERRE_TURNO");

        Assert.NotEmpty(pagina.Data);
        Assert.All(pagina.Data, m => Assert.Equal("CIERRE_TURNO", m.Tipo));
        Assert.Contains(pagina.Data, m => m.Id == $"{TurnoAId}_cierre");
    }

    [Fact]
    public async Task Movimientos_filtro_tipo_stock_pagina_en_db()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var pagina = await GetMovimientosAsync(clientA, "/insumos/movimientos?tipo=DESCUENTO_PEDIDO");

        // Solo movimientos de stock DESCUENTO_PEDIDO de A en cualquier fecha (sin turnos).
        Assert.NotEmpty(pagina.Data);
        Assert.All(pagina.Data, m => Assert.Equal("DESCUENTO_PEDIDO", m.Tipo));
        Assert.DoesNotContain(pagina.Data, m => m.Tipo.EndsWith("_TURNO", StringComparison.Ordinal));
    }

    // ── Helpers HTTP ───────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<DisponibilidadDto[]> PostDisponibilidadAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/insumos/disponibilidad", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<DisponibilidadDto[]>();
        Assert.NotNull(items);
        return items!;
    }

    private static async Task<PagedMovDto> GetMovimientosAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PagedMovDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    // ── Seed helpers ────────────────────────────────────────────────────────────

    private static void SeedNegocio(OrbitDbContext db, string id, string nombre, string slug, DateTime now) =>
        db.Negocios.Add(new Negocio { Id = id, Nombre = nombre, Slug = slug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });

    private static void SeedUser(OrbitDbContext db, IPasswordHasher hasher, string id, string email, string negocioId, DateTime now) =>
        db.Users.Add(new User
        {
            Id = id, Email = email, Password = hasher.Hash(Password), Nombre = "ADMIN", Role = Role.ADMIN,
            Activo = true, NegocioId = negocioId, EmailVerificado = true, CreatedAt = now,
        });

    private static Insumo NuevoInsumo(string id, string nombre, double stock, string negocioId, DateTime now, UnidadMedida unidad = UnidadMedida.UNIDAD) =>
        new() { Id = id, Nombre = nombre, UnidadMedida = unidad, StockActual = stock, StockMinimo = 1, Activo = true, NegocioId = negocioId, CreatedAt = now, UpdatedAt = now };

    private static Extra NuevoExtra(string id, string nombre, double stockActual, string? insumoId, string negocioId, DateTime now) =>
        new()
        {
            Id = id, Nombre = nombre, Precio = 100, StockActual = stockActual, Activo = true, Categoria = "TOPPINGS",
            UnidadMedida = UnidadMedida.UNIDAD, EsGlobal = false, EsPremium = false, InsumoId = insumoId,
            NegocioId = negocioId, CreatedAt = now, UpdatedAt = now,
        };

    private static Aderezo NuevoAderezo(string id, string nombre, double stockActual, string negocioId) =>
        new() { Id = id, Nombre = nombre, Activo = true, StockActual = stockActual, EsGlobal = false, EsPremium = false, Precio = 0, UnidadMedida = UnidadMedida.UNIDAD, NegocioId = negocioId };

    private static StockMovimiento Descuento(string insumoId, double cantidad, string negocioId, DateTime createdAt) =>
        new()
        {
            Id = Guid.NewGuid().ToString(), InsumoId = insumoId, Tipo = "DESCUENTO_PEDIDO", Cantidad = cantidad,
            StockAntes = 0, StockDespues = 0, NegocioId = negocioId, CreatedAt = createdAt,
        };

    private static Turno TurnoCerrado(string id, string userId, string negocioId, double apertura, double cierre, double ventas) =>
        new()
        {
            Id = id, Tipo = TipoTurno.CIERRE, UserId = userId,
            HoraInicio = EnRango, HoraFin = EnRango.AddHours(8),
            CajaAperturaMonto = apertura, CajaCierreMonto = cierre, VentasTotales = ventas,
            CreatedAt = EnRango, NegocioId = negocioId,
        };

    // ── DTOs de lectura ──────────────────────────────────────────────────────────

    private sealed record DisponibilidadDto(string Id, string Nombre, string Tipo, double StockActual, double ConsumoPorUnidad, int Disponible);

    private sealed record ReporteConsumoDto(
        string InsumoId, string Nombre,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] UnidadMedida? UnidadMedida,
        double TotalConsumido, int CantidadMovimientos);

    private sealed record PagedMovDto(List<MovUnificadoDto> Data, int Total, int Page, int TotalPages);

    private sealed record MovUnificadoDto(
        string Id, string Tipo, string? ConfirmadoPor,
        double? MontoReal, double? MontoEsperado, double? Diferencia);
}
