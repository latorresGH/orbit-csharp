using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Turnos;

/// <summary>
/// Tests de integración del módulo Turnos. Verifica el diseño divergente respecto al NestJS (turno GLOBAL por
/// negocio): un único turno activo por negocio, guard de "ya hay uno abierto", cálculo de
/// ventas/efectivo/diferencia al cerrar, y aislamiento multi-tenant.
/// </summary>
[Collection(TurnoApiCollection.Name)]
public sealed class TurnosIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-tu-a";
    private const string NegocioBId = "neg-tu-b";
    private const string NegocioASlug = "negocio-tu-a";
    private const string NegocioBSlug = "negocio-tu-b";
    private const string AdminAId = "user-tu-a";
    private const string AdminAEmail = "admin-a@tu.test";
    private const string TrabajadorAId = "user-tu-a-trab";
    private const string TrabajadorAEmail = "trab-a@tu.test";
    private const string AdminBEmail = "admin-b@tu.test";

    private readonly TurnoWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio TU A", Slug = NegocioASlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Negocios.Add(new Negocio { Id = NegocioBId, Nombre = "Negocio TU B", Slug = NegocioBSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = AdminAId, Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = TrabajadorAId, Email = TrabajadorAEmail, Password = hasher.Hash(Password), Nombre = "Trabajador A", Role = Role.TRABAJADOR, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-tu-b", Email = AdminBEmail, Password = hasher.Hash(Password), Nombre = "Admin B", Role = Role.ADMIN, Activo = true, NegocioId = NegocioBId, EmailVerificado = true, CreatedAt = now });

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
    public async Task Abrir_turno_y_guard_de_ya_activo()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var abrir = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 10000, notas = "Turno noche" });
        Assert.Equal(HttpStatusCode.OK, abrir.StatusCode);
        var turno = (await abrir.Content.ReadFromJsonAsync<TurnoDto>())!;
        Assert.Equal(AdminAId, turno.UserId);
        Assert.Equal(10000, turno.CajaAperturaMonto);
        Assert.Null(turno.HoraFin);

        // Segundo intento (mismo negocio) → 400: ya hay un turno activo.
        var otra = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);

        // El guard es por negocio, no por usuario: otro empleado del mismo negocio tampoco puede abrir.
        var clientTrab = await LoginAsync(TrabajadorAEmail, NegocioASlug);
        var trabIntento = await clientTrab.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, trabIntento.StatusCode);

        // Se creó el CajaMovimiento de apertura.
        await UsingDbAsync(async db =>
        {
            var aperturas = await db.CajaMovimientos.IgnoreQueryFilters()
                .Where(m => m.NegocioId == NegocioAId && m.Tipo == TipoMovimientoCaja.APERTURA_TURNO).ToListAsync();
            Assert.Single(aperturas);
            Assert.Equal(10000, aperturas[0].MontoTotal);
        });
    }

    [Fact]
    public async Task Cerrar_turno_calcula_ventas_efectivo_y_diferencia()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var abrir = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 10000 });
        Assert.Equal(HttpStatusCode.OK, abrir.StatusCode);
        var turno = (await abrir.Content.ReadFromJsonAsync<TurnoDto>())!;

        // Movimientos dentro de la ventana del turno: fechados "ahora" (entre la apertura y el cierre).
        var dentro = Now();
        await UsingDbAsync(async db =>
        {
            // ENTRADA manual (efectivo, sin pedido): +5000 al efectivo y +5000 a ventas.
            db.CajaMovimientos.Add(NuevoMov(NegocioAId, TipoMovimientoCaja.ENTRADA, montoTotal: 5000, gananciaNegocio: 5000, fecha: dentro));
            // SALIDA (gasto): −1000 del efectivo.
            db.CajaMovimientos.Add(NuevoMov(NegocioAId, TipoMovimientoCaja.SALIDA, montoTotal: 1000, gananciaNegocio: -1000, fecha: dentro));
            await db.SaveChangesAsync();
        });

        // efectivoEsperado = 5000 (entrada manual) − 1000 (salida) = 4000
        // esperado = apertura 10000 + 4000 = 14000; contamos 13000 → diferencia −1000 (alerta).
        var cerrar = await client.PostAsJsonAsync("/turnos/cerrar", new { montoFinal = 13000, notas = "Faltó plata" });
        Assert.Equal(HttpStatusCode.OK, cerrar.StatusCode);
        var cierre = (await cerrar.Content.ReadFromJsonAsync<CierreDto>())!;

        Assert.Equal(14000, cierre.MontoEsperado);
        Assert.Equal(-1000, cierre.Diferencia);
        Assert.True(cierre.AlertaDiferencia);
        Assert.Equal(5000, cierre.Turno.VentasTotales);   // Σ gananciaNegocio de ENTRADAS
        Assert.Equal(13000, cierre.Turno.CajaCierreMonto);
        Assert.NotNull(cierre.Turno.HoraFin);

        await UsingDbAsync(async db =>
        {
            // Se persistió el turno cerrado y su CajaMovimiento de cierre.
            var cierres = await db.CajaMovimientos.IgnoreQueryFilters()
                .Where(m => m.NegocioId == NegocioAId && m.Tipo == TipoMovimientoCaja.CIERRE_TURNO).ToListAsync();
            Assert.Single(cierres);
            var t = await db.Turnos.IgnoreQueryFilters().FirstAsync(x => x.Id == turno.Id);
            Assert.NotNull(t.HoraFin);
            Assert.Equal(14000, t.MontoEsperado);
        });

        // Cerrado el turno, se puede abrir uno nuevo.
        var reabrir = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 2000 });
        Assert.Equal(HttpStatusCode.OK, reabrir.StatusCode);
    }

    [Fact]
    public async Task Cerrar_sin_turno_abierto_devuelve_400()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        var cerrar = await client.PostAsJsonAsync("/turnos/cerrar", new { montoFinal = 1000 });
        Assert.Equal(HttpStatusCode.BadRequest, cerrar.StatusCode);
    }

    [Fact]
    public async Task Activo_devuelve_ventas_en_vivo()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        // Sin turno: 204 No Content.
        var vacio = await client.GetAsync("/turnos/activo");
        Assert.Equal(HttpStatusCode.NoContent, vacio.StatusCode);

        var abrir = await client.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 8000 });
        var turno = (await abrir.Content.ReadFromJsonAsync<TurnoDto>())!;

        await UsingDbAsync(async db =>
        {
            db.CajaMovimientos.Add(NuevoMov(NegocioAId, TipoMovimientoCaja.ENTRADA, montoTotal: 3000, gananciaNegocio: 3000, fecha: Now()));
            await db.SaveChangesAsync();
        });

        var activo = (await client.GetFromJsonAsync<TurnoActivoDto>("/turnos/activo"))!;
        Assert.Equal(turno.Id, activo.Id);
        Assert.Equal(3000, activo.VentasEnVivo);
        Assert.Equal(11000, activo.MontoEsperado); // 8000 apertura + 3000 efectivo
    }

    [Fact]
    public async Task Cerrar_es_solo_admin()
    {
        var admin = await LoginAsync(AdminAEmail, NegocioASlug);
        await admin.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 1000 });

        // TRABAJADOR puede ver el activo pero no cerrar.
        var trab = await LoginAsync(TrabajadorAEmail, NegocioASlug);
        var activo = await trab.GetAsync("/turnos/activo");
        Assert.Equal(HttpStatusCode.OK, activo.StatusCode);

        var cerrar = await trab.PostAsJsonAsync("/turnos/cerrar", new { montoFinal = 1000 });
        Assert.Equal(HttpStatusCode.Forbidden, cerrar.StatusCode);
    }

    [Fact]
    public async Task Aislamiento_multitenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var abrirA = await clientA.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 10000 });
        var turnoA = (await abrirA.Content.ReadFromJsonAsync<TurnoDto>())!;

        // B no ve el turno activo de A (turno global es por-negocio) y puede abrir el suyo.
        var activoB = await clientB.GetAsync("/turnos/activo");
        Assert.Equal(HttpStatusCode.NoContent, activoB.StatusCode);

        var abrirB = await clientB.PostAsJsonAsync("/turnos/abrir", new { montoInicial = 500 });
        Assert.Equal(HttpStatusCode.OK, abrirB.StatusCode);

        // El historial de B no incluye el turno de A.
        var histB = (await clientB.GetFromJsonAsync<PaginatedDto<TurnoDto>>("/turnos/historial"))!;
        Assert.DoesNotContain(histB.Data, t => t.Id == turnoA.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CajaMovimiento NuevoMov(string negocioId, TipoMovimientoCaja tipo, double montoTotal, double gananciaNegocio, DateTime fecha) => new()
    {
        Id = Guid.NewGuid().ToString(),
        NegocioId = negocioId,
        Tipo = tipo,
        MontoTotal = montoTotal,
        GananciaNegocio = gananciaNegocio,
        GananciaRepartidor = 0,
        Descripcion = "seed",
        FechaConfirmacion = fecha,
        Anulado = false,
        CreatedAt = fecha,
    };

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

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private sealed record TurnoDto(
        string Id, string UserId, string? UserNombre, DateTime HoraInicio, DateTime? HoraFin,
        double CajaAperturaMonto, double? CajaCierreMonto, double VentasTotales,
        double? MontoEsperado, double? Diferencia, bool AlertaDiferencia, string? Notas, DateTime CreatedAt);

    private sealed record TurnoActivoDto(
        string Id, string UserId, string? UserNombre, DateTime HoraInicio,
        double CajaAperturaMonto, double VentasEnVivo, double MontoEsperado, string? Notas, DateTime CreatedAt);

    private sealed record CierreDto(TurnoDto Turno, double MontoEsperado, double Diferencia, bool AlertaDiferencia);

    private sealed record PaginatedDto<T>(List<T> Data, int Total, int Page, int TotalPages);
}
