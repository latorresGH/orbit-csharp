using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Negocios;

/// <summary>
/// Tests de integración del módulo Negocio: registro público + verificación de email por código (con el gate
/// por-request de emailVerificado), reenvío anti-spam, slug 409, perfil propio, cierre de cuenta con gracia,
/// gestión SUPERADMIN y purga de cuentas cerradas.
/// </summary>
[Collection(NegocioApiCollection.Name)]
public sealed class NegocioIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-ng-a";
    private const string NegocioASlug = "negocio-ng-a";
    private const string AdminAEmail = "admin-a@ng.test";
    private const string SuperEmail = "super@ng.test";

    private readonly NegocioWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio NG A", Slug = NegocioASlug, Activo = true, Plan = "trial", TrialExpira = now.AddDays(5), CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = "user-ng-a", Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        // SUPERADMIN: sin negocio, verificado.
        db.Users.Add(new User { Id = "user-super", Email = SuperEmail, Password = hasher.Hash(Password), Nombre = "Super", Role = Role.SUPERADMIN, Activo = true, NegocioId = null, EmailVerificado = true, CreatedAt = now });

        await db.SaveChangesAsync();

        // Role.SUPERADMIN es el valor 0 del enum → sentinel de EF: en el INSERT se omite y la columna toma el
        // default de la DB ('TRABAJADOR'). Se fuerza con un UPDATE directo. Ver [[ef-enum-sentinel-warnings]].
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == "user-super")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, Role.SUPERADMIN));
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
    public async Task Registro_crea_negocio_admin_configs_y_demora()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/negocio/registro", new
        {
            nombreNegocio = "Pizzería Test",
            slug = "pizzeria-test",
            nombreAdmin = "Juan",
            email = "juan@pizzeria.test",
            password = "password123",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var pend = (await resp.Content.ReadFromJsonAsync<RegistroPendienteDto>())!;
        Assert.True(pend.EmailPendiente);
        Assert.Equal("pizzeria-test", pend.Slug);

        await QueryDbAsync(async db =>
        {
            var negocio = await db.Negocios.IgnoreQueryFilters().FirstAsync(n => n.Slug == "pizzeria-test");
            Assert.Equal("trial", negocio.Plan);
            Assert.NotNull(negocio.TrialExpira);

            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.NegocioId == negocio.Id);
            Assert.Equal("juan@pizzeria.test", user.Email);
            Assert.False(user.EmailVerificado);
            Assert.NotNull(user.CodigoVerificacion);

            Assert.Equal(15, await db.Configuracions.IgnoreQueryFilters().CountAsync(c => c.NegocioId == negocio.Id));
            Assert.Equal(1, await db.DemoraConfigs.IgnoreQueryFilters().CountAsync(d => d.NegocioId == negocio.Id));
            return 0;
        });
    }

    [Fact]
    public async Task Registro_slug_duplicado_409()
    {
        var anon = _factory.CreateClient();
        var body = new { nombreNegocio = "Uno", slug = "duplicado-x", nombreAdmin = "Ana", email = "a@x.test", password = "password123" };
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/negocio/registro", body)).StatusCode);

        var dup = await anon.PostAsJsonAsync("/negocio/registro", new { nombreNegocio = "Dos", slug = "duplicado-x", nombreAdmin = "Beto", email = "b@x.test", password = "password123" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Verificacion_gate_bloquea_hasta_verificar()
    {
        var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/negocio/registro", new { nombreNegocio = "Gate", slug = "gate-test", nombreAdmin = "Gonza", email = "g@gate.test", password = "password123" });

        // Login funciona (no chequea verificado), pero el gate por-request bloquea las llamadas autenticadas.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/login", new { email = "g@gate.test", password = "password123", negocioSlug = "gate-test" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var bloqueado = await client.GetAsync("/negocio/mi-estado");
        Assert.Equal(HttpStatusCode.Unauthorized, bloqueado.StatusCode);

        // Código incorrecto → 400.
        var malo = await client.PostAsJsonAsync("/negocio/verificar-email", new { email = "g@gate.test", negocioSlug = "gate-test", codigo = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, malo.StatusCode);

        // Código correcto (leído de la DB) → 200 + sesión; ya no bloquea.
        var codigo = await QueryDbAsync(db => db.Users.IgnoreQueryFilters()
            .Where(u => u.Email == "g@gate.test").Select(u => u.CodigoVerificacion!).FirstAsync());
        var ok = await client.PostAsJsonAsync("/negocio/verificar-email", new { email = "g@gate.test", negocioSlug = "gate-test", codigo });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var ahora = await client.GetAsync("/negocio/mi-estado");
        Assert.Equal(HttpStatusCode.OK, ahora.StatusCode);

        await QueryDbAsync(async db =>
        {
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == "g@gate.test");
            Assert.True(user.EmailVerificado);
            Assert.Null(user.CodigoVerificacion);
            return 0;
        });
    }

    [Fact]
    public async Task Verificacion_lockout_progresivo()
    {
        var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/negocio/registro", new { nombreNegocio = "Lock", slug = "lock-test", nombreAdmin = "Lucas", email = "l@lock.test", password = "password123" });

        // 3 intentos con código incorrecto → el 3ro dispara el bloqueo (umbral 3 → 5 min).
        for (var i = 0; i < 3; i++)
        {
            var r = await anon.PostAsJsonAsync("/negocio/verificar-email", new { email = "l@lock.test", negocioSlug = "lock-test", codigo = "999999" });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        // El 4to intento ya está bloqueado: mensaje distinto ("Demasiados intentos").
        var bloqueado = await anon.PostAsJsonAsync("/negocio/verificar-email", new { email = "l@lock.test", negocioSlug = "lock-test", codigo = "999999" });
        Assert.Equal(HttpStatusCode.BadRequest, bloqueado.StatusCode);
        var msg = await bloqueado.Content.ReadAsStringAsync();
        Assert.Contains("Demasiados intentos", msg);
    }

    [Fact]
    public async Task Verificacion_codigo_vencido()
    {
        var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/negocio/registro", new { nombreNegocio = "Venc", slug = "venc-test", nombreAdmin = "Vera", email = "v@venc.test", password = "password123" });

        // Vencemos el código manualmente.
        var codigo = await QueryDbAsync(async db =>
        {
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == "v@venc.test");
            var cod = user.CodigoVerificacion!;
            user.CodigoExpira = Now().AddMinutes(-1);
            await db.SaveChangesAsync();
            return cod;
        });

        var resp = await anon.PostAsJsonAsync("/negocio/verificar-email", new { email = "v@venc.test", negocioSlug = "venc-test", codigo });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("venció", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reenviar_codigo_anti_spam()
    {
        var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/negocio/registro", new { nombreNegocio = "Re", slug = "re-test", nombreAdmin = "Rita", email = "r@re.test", password = "password123" });

        // Recién registrado: el código vence en ~15 min → reenviar inmediato es rechazado.
        var resp = await anon.PostAsJsonAsync("/negocio/reenviar-codigo", new { email = "r@re.test", negocioSlug = "re-test" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Check_slug_e_info_publica()
    {
        var anon = _factory.CreateClient();

        var libre = (await anon.GetFromJsonAsync<SlugDto>("/negocio/check-slug?slug=libre-nuevo"))!;
        Assert.True(libre.Disponible);
        var tomado = (await anon.GetFromJsonAsync<SlugDto>($"/negocio/check-slug?slug={NegocioASlug}"))!;
        Assert.False(tomado.Disponible);

        var info = (await anon.GetFromJsonAsync<InfoDto>($"/negocio/info-publica?slug={NegocioASlug}"))!;
        Assert.Equal("Negocio NG A", info.Nombre);

        var noExiste = await anon.GetAsync("/negocio/info-publica?slug=no-existe");
        Assert.Equal(HttpStatusCode.NotFound, noExiste.StatusCode);
    }

    [Fact]
    public async Task Mi_perfil_leer_y_editar()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var perfil = (await client.GetFromJsonAsync<PerfilDto>("/negocio/mi-perfil"))!;
        Assert.Equal("Negocio NG A", perfil.Negocio.Nombre);
        Assert.Equal(AdminAEmail, perfil.User.Email);

        // Logo no-https → 400.
        var malo = await client.PatchAsJsonAsync("/negocio/mi-perfil", new { logoUrl = "http://inseguro.test/logo.png" });
        Assert.Equal(HttpStatusCode.BadRequest, malo.StatusCode);

        // Edición válida.
        var ok = await client.PatchAsJsonAsync("/negocio/mi-perfil", new { nombreNegocio = "Nuevo Nombre", logoUrl = "https://cdn.test/logo.png" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var actualizado = (await ok.Content.ReadFromJsonAsync<PerfilDto>())!;
        Assert.Equal("Nuevo Nombre", actualizado.Negocio.Nombre);
        Assert.Equal("https://cdn.test/logo.png", actualizado.Negocio.LogoUrl);
    }

    [Fact]
    public async Task Cerrar_cuenta_y_estado_cierre()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var antes = (await client.GetFromJsonAsync<EstadoCierreDto>("/negocio/estado-cierre"))!;
        Assert.False(antes.Cerrada);

        var cerrar = await client.PostAsync("/negocio/cerrar-cuenta", null);
        Assert.Equal(HttpStatusCode.OK, cerrar.StatusCode);

        await QueryDbAsync(async db =>
        {
            var n = await db.Negocios.IgnoreQueryFilters().FirstAsync(x => x.Id == NegocioAId);
            Assert.False(n.Activo);
            Assert.NotNull(n.CuentaCerradaAt);
            return 0;
        });

        // Re-login (el user sigue activo) y consulta del período de gracia.
        var client2 = await LoginAsync(AdminAEmail, NegocioASlug);
        var estado = (await client2.GetFromJsonAsync<EstadoCierreDto>("/negocio/estado-cierre"))!;
        Assert.True(estado.Cerrada);
        Assert.True(estado.DiasRestantes is >= 15 and <= 16);
    }

    [Fact]
    public async Task Superadmin_gestion_de_negocios()
    {
        var super = await LoginSuperAsync();

        // Alta manual (admin queda verificado).
        var crear = await super.PostAsJsonAsync("/negocio", new
        {
            nombre = "Sucursal SA", slug = "sucursal-sa", adminEmail = "sa@sa.test", adminPassword = "password123", adminNombre = "Admin SA",
        });
        Assert.Equal(HttpStatusCode.Created, crear.StatusCode);
        var creado = (await crear.Content.ReadFromJsonAsync<NegocioCreadoDto>())!;

        // Listado incluye el negocio con su admin y estadoPlan.
        var lista = (await super.GetFromJsonAsync<List<NegocioListDto>>("/negocio"))!;
        var item = Assert.Single(lista, n => n.Id == creado.Id);
        Assert.Equal("Admin SA", item.Admin!.Nombre);
        Assert.Equal("trial_activo", item.EstadoPlan);

        // Activar plan → plan=activo, sin trial.
        var activar = await super.PostAsync($"/negocio/{creado.Id}/activar", null);
        Assert.Equal(HttpStatusCode.OK, activar.StatusCode);
        var activo = (await activar.Content.ReadFromJsonAsync<NegocioDetalleDto>())!;
        Assert.Equal("activo", activo.Plan);
        Assert.Null(activo.TrialExpira);

        // Extender trial → vuelve a trial con nueva expiración.
        var extender = await super.PostAsJsonAsync($"/negocio/{creado.Id}/extender-trial", new { dias = 10 });
        Assert.Equal(HttpStatusCode.OK, extender.StatusCode);
        var extendido = (await extender.Content.ReadFromJsonAsync<NegocioDetalleDto>())!;
        Assert.Equal("trial", extendido.Plan);
        Assert.NotNull(extendido.TrialExpira);

        // Desactivar.
        var desactivar = await super.PostAsync($"/negocio/{creado.Id}/desactivar", null);
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);
        Assert.False((await desactivar.Content.ReadFromJsonAsync<NegocioDetalleDto>())!.Activo);
    }

    [Fact]
    public async Task Superadmin_endpoints_prohibidos_para_admin()
    {
        var admin = await LoginAsync(AdminAEmail, NegocioASlug);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/negocio")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/negocio/limpiar-cerradas", null)).StatusCode);
    }

    [Fact]
    public async Task Limpiar_cerradas_purga_solo_las_vencidas()
    {
        var super = await LoginSuperAsync();

        // Dos negocios creados por SUPERADMIN.
        var viejo = (await (await super.PostAsJsonAsync("/negocio", new { nombre = "Viejo", slug = "viejo-cerrado", adminEmail = "v@old.test", adminPassword = "password123", adminNombre = "V" })).Content.ReadFromJsonAsync<NegocioCreadoDto>())!;
        var reciente = (await (await super.PostAsJsonAsync("/negocio", new { nombre = "Reciente", slug = "reciente-cerrado", adminEmail = "r@old.test", adminPassword = "password123", adminNombre = "R" })).Content.ReadFromJsonAsync<NegocioCreadoDto>())!;

        // Uno cerrado hace 17 días (purgable); otro cerrado hace 5 días (dentro de gracia).
        await QueryDbAsync(async db =>
        {
            var v = await db.Negocios.IgnoreQueryFilters().FirstAsync(n => n.Id == viejo.Id);
            v.Activo = false; v.CuentaCerradaAt = Now().AddDays(-17);
            var r = await db.Negocios.IgnoreQueryFilters().FirstAsync(n => n.Id == reciente.Id);
            r.Activo = false; r.CuentaCerradaAt = Now().AddDays(-5);
            await db.SaveChangesAsync();
            return 0;
        });

        var resp = await super.PostAsync("/negocio/limpiar-cerradas", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<LimpiezaDto>())!;
        Assert.Equal(1, result.Eliminados);

        await QueryDbAsync(async db =>
        {
            Assert.False(await db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Id == viejo.Id));
            Assert.Equal(0, await db.Configuracions.IgnoreQueryFilters().CountAsync(c => c.NegocioId == viejo.Id));
            Assert.Equal(0, await db.Users.IgnoreQueryFilters().CountAsync(u => u.NegocioId == viejo.Id));
            Assert.True(await db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Id == reciente.Id));
            return 0;
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<HttpClient> LoginSuperAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email = SuperEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<T> QueryDbAsync<T>(Func<OrbitDbContext, Task<T>> fn)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return await fn(db);
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private sealed record RegistroPendienteDto(bool EmailPendiente, string Email, string Slug);
    private sealed record SlugDto(bool Disponible);
    private sealed record InfoDto(string Id, string Nombre, string Slug, string? LogoUrl);
    private sealed record EstadoCierreDto(bool Cerrada, int? DiasRestantes);
    private sealed record PerfilNegocioDto(string Id, string Nombre, string Slug, string? LogoUrl, string Plan, string EstadoPlan);
    private sealed record PerfilUserDto(string Id, string Email, string Nombre, string Role);
    private sealed record PerfilDto(PerfilNegocioDto Negocio, PerfilUserDto User);
    private sealed record NegocioCreadoDto(string Id, string Nombre, string Slug, string Plan, DateTime? TrialExpira);
    private sealed record NegocioAdminDto(string Id, string Nombre, string Email);
    private sealed record NegocioListDto(string Id, string Nombre, string Slug, bool Activo, string Plan, string EstadoPlan, NegocioAdminDto? Admin);
    private sealed record NegocioDetalleDto(string Id, string Nombre, string Slug, bool Activo, string Plan, DateTime? TrialExpira);
    private sealed record LimpiezaDto(int Eliminados);
}
