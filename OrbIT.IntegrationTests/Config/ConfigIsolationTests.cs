using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Application.Common;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Config;

/// <summary>
/// Tests de integración de Config: listado público por slug, validadores de clave, upsert, la regla de
/// apertura≠cierre, el endpoint público de horario (reusando <see cref="HorarioComercial"/>) y aislamiento
/// multi-tenant.
/// </summary>
[Collection(ConfigApiCollection.Name)]
public sealed class ConfigIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";
    private const string NegocioAId = "neg-cfg-a";
    private const string NegocioBId = "neg-cfg-b";
    private const string NegocioASlug = "negocio-cfg-a";
    private const string NegocioBSlug = "negocio-cfg-b";
    private const string AdminAEmail = "admin-a@cfg.test";
    private const string AdminBEmail = "admin-b@cfg.test";

    private readonly ConfigWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioAId, Nombre = "Negocio CFG A", Slug = NegocioASlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Negocios.Add(new Negocio { Id = NegocioBId, Nombre = "Negocio CFG B", Slug = NegocioBSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = "user-cfg-a", Email = AdminAEmail, Password = hasher.Hash(Password), Nombre = "Admin A", Role = Role.ADMIN, Activo = true, NegocioId = NegocioAId, EmailVerificado = true, CreatedAt = now });
        db.Users.Add(new User { Id = "user-cfg-b", Email = AdminBEmail, Password = hasher.Hash(Password), Nombre = "Admin B", Role = Role.ADMIN, Activo = true, NegocioId = NegocioBId, EmailVerificado = true, CreatedAt = now });

        // Config de A: dos claves. Config de B: una (para aislamiento).
        db.Configuracions.Add(NuevaConfig(NegocioAId, "theme", "orbit", now));
        db.Configuracions.Add(NuevaConfig(NegocioAId, "delivery_precio_base", "3000", now));
        db.Configuracions.Add(NuevaConfig(NegocioBId, "theme", "minimal", now));

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
    public async Task Listado_publico_por_slug()
    {
        // Cliente anónimo (sin login) con ?negocio=slug.
        var anon = _factory.CreateClient();
        var configs = (await anon.GetFromJsonAsync<List<ConfigItemDto>>($"/config?negocio={NegocioASlug}"))!;

        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.Clave == "theme" && c.Valor == "orbit");
        // Orden alfabético por clave: delivery_precio_base antes que theme.
        Assert.Equal("delivery_precio_base", configs[0].Clave);
    }

    [Fact]
    public async Task Establecer_valida_y_upsertea()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        // Validador: modo_mesas solo acepta true/false.
        var invalido = await client.PostAsJsonAsync("/config/modo_mesas", new { valor = "quizas" });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        var valido = await client.PostAsJsonAsync("/config/modo_mesas", new { valor = "true" });
        Assert.Equal(HttpStatusCode.OK, valido.StatusCode);

        // Upsert: re-setear la misma clave actualiza el valor, no duplica.
        var actualizado = await client.PostAsJsonAsync("/config/modo_mesas", new { valor = "false" });
        Assert.Equal(HttpStatusCode.OK, actualizado.StatusCode);

        var leido = (await client.GetFromJsonAsync<ConfigValorDto>("/config/modo_mesas"))!;
        Assert.Equal("false", leido.Valor);

        await UsingDbAsync(async db =>
        {
            var count = await db.Configuracions.IgnoreQueryFilters()
                .CountAsync(c => c.NegocioId == NegocioAId && c.Clave == "modo_mesas");
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public async Task Apertura_y_cierre_no_pueden_ser_iguales()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);

        var apertura = await client.PostAsJsonAsync("/config/hora_apertura", new { valor = "21:00" });
        Assert.Equal(HttpStatusCode.OK, apertura.StatusCode);

        // Mismo valor para cierre → 400.
        var cierreIgual = await client.PostAsJsonAsync("/config/hora_cierre", new { valor = "21:00" });
        Assert.Equal(HttpStatusCode.BadRequest, cierreIgual.StatusCode);

        // Formato inválido → 400.
        var formato = await client.PostAsJsonAsync("/config/hora_cierre", new { valor = "25:99" });
        Assert.Equal(HttpStatusCode.BadRequest, formato.StatusCode);

        var cierreOk = await client.PostAsJsonAsync("/config/hora_cierre", new { valor = "23:30" });
        Assert.Equal(HttpStatusCode.OK, cierreOk.StatusCode);
    }

    [Fact]
    public async Task Horario_sin_config_devuelve_abierto_por_defecto()
    {
        var anon = _factory.CreateClient();
        var horario = (await anon.GetFromJsonAsync<HorarioDto>($"/config/horario/abierto?negocio={NegocioASlug}"))!;

        Assert.True(horario.Abierto);
        Assert.Equal("abierto", horario.Razon);
        Assert.Null(horario.HoraApertura);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, horario.DiasAtencion);
    }

    [Fact]
    public async Task Horario_dia_no_laboral_cerrado()
    {
        var client = await LoginAsync(AdminAEmail, NegocioASlug);
        await client.PostAsJsonAsync("/config/hora_apertura", new { valor = "08:00" });
        await client.PostAsJsonAsync("/config/hora_cierre", new { valor = "23:00" });

        // dias_atencion = un solo día que NO es hoy (AR) → dia_no_laboral, cerrado, determinístico.
        var hoy = ArgentinaClock.DiaSemana(ArgentinaClock.Now());
        var otroDia = hoy == 1 ? 2 : 1;
        await client.PostAsJsonAsync("/config/dias_atencion", new { valor = otroDia.ToString() });

        var anon = _factory.CreateClient();
        var horario = (await anon.GetFromJsonAsync<HorarioDto>($"/config/horario/abierto?negocio={NegocioASlug}"))!;

        Assert.False(horario.Abierto);
        Assert.Equal("dia_no_laboral", horario.Razon);
        Assert.NotNull(horario.ProximaApertura);
        Assert.Equal(otroDia, horario.ProximaApertura!.Dia);
    }

    [Fact]
    public async Task Aislamiento_multitenant()
    {
        // El listado público de B no ve la config de A.
        var anon = _factory.CreateClient();
        var configsB = (await anon.GetFromJsonAsync<List<ConfigItemDto>>($"/config?negocio={NegocioBSlug}"))!;
        Assert.Single(configsB);
        Assert.Equal("minimal", configsB[0].Valor);

        // Slug inexistente → 404 (lo resuelve el resource filter del atributo).
        var noExiste = await anon.GetAsync("/config?negocio=no-existe");
        Assert.Equal(HttpStatusCode.NotFound, noExiste.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Configuracion NuevaConfig(string negocioId, string clave, string valor, DateTime now) => new()
    {
        Id = Guid.NewGuid().ToString(),
        NegocioId = negocioId,
        Clave = clave,
        Valor = valor,
        CreatedAt = now,
        UpdatedAt = now,
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

    private sealed record ConfigItemDto(string Id, string Clave, string Valor, string? Descripcion);

    private sealed record ConfigValorDto(string Clave, string? Valor);

    private sealed record ProximaAperturaDto(int Dia, string Hora, string DiaNombre);

    private sealed record HorarioDto(
        bool Abierto, string? HoraApertura, string? HoraCierre, string HoraActual,
        List<int> DiasAtencion, string Razon, ProximaAperturaDto? ProximaApertura);
}
