using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Auth;

/// <summary>
/// Tests de las piezas aisladas de Google OAuth (el roundtrip con Google no se puede ejercer sin credenciales
/// reales): OTT single-use vía <c>/auth/google/exchange</c>, expiración/invalidez del OTT, y el alta
/// <c>/auth/google/registro</c> (password placeholder, email verificado, sin código). El OTT se genera llamando
/// a <see cref="IGoogleAuthService"/> directamente (lo que hace el callback real) o sembrando <c>TempToken</c>.
/// </summary>
[Collection(GoogleAuthApiCollection.Name)]
public sealed class GoogleAuthTests : IAsyncLifetime
{
    private const string NegocioId = "neg-goog";
    private const string NegocioSlug = "negocio-goog";
    private const string AdminId = "user-goog-admin";
    private const string AdminEmail = "admin@goog.test";

    private readonly GoogleAuthWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = Now();
        db.Negocios.Add(new Negocio { Id = NegocioId, Nombre = "Negocio Goog", Slug = NegocioSlug, Activo = true, Plan = "basic", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new User { Id = AdminId, Email = AdminEmail, Password = hasher.Hash("x"), Nombre = "Admin Goog", Role = Role.ADMIN, Activo = true, NegocioId = NegocioId, EmailVerificado = true, CreatedAt = now });
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
    public async Task Ott_valido_se_canjea_una_sola_vez()
    {
        // El callback real llama a ResolveLoginAsync; acá lo hacemos directo para obtener el OTT crudo.
        var ott = await GenerarOttAsync();

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // Primer canje: OK + setea cookies de sesión.
        var first = await client.PostAsJsonAsync("/auth/google/exchange", new { ott });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var setCookies = first.Headers.TryGetValues("Set-Cookie", out var vals) ? string.Join(";", vals) : "";
        Assert.Contains("access_token", setCookies);
        Assert.Contains("refresh_token", setCookies);

        // El usuario devuelto es el ADMIN del negocio.
        var user = (await first.Content.ReadFromJsonAsync<ExchangeResponse>())!.User;
        Assert.Equal(AdminEmail, user.Email);
        Assert.Equal("ADMIN", user.Role);

        // Se persistió el refresh token de la sesión.
        await UsingDbAsync(async db =>
            Assert.True(await db.RefreshTokens.IgnoreQueryFilters().AnyAsync(t => t.UserId == AdminId)));

        // Segundo canje con el MISMO ott: 401 (single-use).
        var second = await client.PostAsJsonAsync("/auth/google/exchange", new { ott });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        // La fila del OTT quedó marcada como usada.
        await UsingDbAsync(async db =>
        {
            var tokens = await db.TempTokens.IgnoreQueryFilters().Where(t => t.UserId == AdminId).ToListAsync();
            Assert.Single(tokens);
            Assert.True(tokens[0].Usada);
        });
    }

    [Fact]
    public async Task Ott_expirado_devuelve_401()
    {
        // Sembramos un TempToken ya vencido (hash de un token crudo conocido).
        const string rawOtt = "ott-crudo-vencido-123";
        string tokenHash;
        using (var scope = _factory.Services.CreateScope())
        {
            tokenHash = scope.ServiceProvider.GetRequiredService<IJwtTokenService>().ComputeTokenHash(rawOtt);
        }
        await UsingDbAsync(async db =>
        {
            db.TempTokens.Add(new TempToken
            {
                Id = Guid.NewGuid().ToString(),
                TokenHash = tokenHash,
                UserId = AdminId,
                NegocioId = NegocioId,
                ExpiresAt = Now().AddSeconds(-1), // ya expiró
                Usada = false,
                CreatedAt = Now().AddSeconds(-31),
            });
            await db.SaveChangesAsync();
        });

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/google/exchange", new { ott = rawOtt });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Ott_invalido_devuelve_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/google/exchange", new { ott = "no-existe-este-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Registro_google_crea_admin_verificado_con_password_no_vacio()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var resp = await client.PostAsJsonAsync("/auth/google/registro", new
        {
            email = "nuevo@goog.test",
            nombre = "Dueño Nuevo",
            nombreNegocio = "Pizzería Nueva",
            slug = "pizzeria-nueva",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Emite sesión de inmediato (cookies).
        var setCookies = resp.Headers.TryGetValues("Set-Cookie", out var vals) ? string.Join(";", vals) : "";
        Assert.Contains("access_token", setCookies);
        Assert.Contains("refresh_token", setCookies);

        await UsingDbAsync(async db =>
        {
            var negocio = await db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Slug == "pizzeria-nueva");
            Assert.NotNull(negocio);
            Assert.Equal("trial", negocio!.Plan);

            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == "nuevo@goog.test");
            Assert.NotNull(user);
            Assert.Equal(Role.ADMIN, user!.Role);
            Assert.True(user.EmailVerificado);              // Google ya verificó el email
            Assert.False(string.IsNullOrWhiteSpace(user.Password)); // password placeholder no vacío (hash bcrypt)
            Assert.Equal(negocio.Id, user.NegocioId);

            // Se sembraron las configs por defecto (paridad con el registro normal).
            Assert.True(await db.Configuracions.IgnoreQueryFilters().AnyAsync(c => c.NegocioId == negocio.Id));
        });
    }

    [Fact]
    public async Task Registro_google_con_slug_existente_devuelve_409()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/google/registro", new
        {
            email = "otro@goog.test",
            nombre = "Otro",
            nombreNegocio = "Colisión",
            slug = NegocioSlug, // ya existe
        });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GenerarOttAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var google = scope.ServiceProvider.GetRequiredService<IGoogleAuthService>();
        var result = await google.ResolveLoginAsync(AdminEmail, "Admin Goog", NegocioSlug);
        Assert.Equal(GoogleLoginTipo.Login, result.Tipo);
        Assert.NotNull(result.Ott);
        return result.Ott!;
    }

    private async Task UsingDbAsync(Func<OrbitDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        await action(db);
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private sealed record ExchangeResponse(UserDto User);
    private sealed record UserDto(string Id, string Email, string Nombre, string Role);
}
