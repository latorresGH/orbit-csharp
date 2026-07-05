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
/// Test de integración del flujo completo de auth contra la Api real (in-memory) y una
/// base PostgreSQL dedicada: login → request autenticada (/auth/me) → refresh (con rotación)
/// → logout. Verifica cookies, claims y revocación de refresh tokens en DB.
/// </summary>
public sealed class AuthFlowIntegrationTests : IAsyncLifetime
{
    private const string Email = "admin@negocio-auth.test";
    private const string Password = "secret-password-123";
    private const string NegocioSlug = "negocio-auth";
    private const string NegocioId = "neg-auth";
    private const string UserId = "user-auth-admin";

    private readonly AuthWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        db.Negocios.Add(new Negocio
        {
            Id = NegocioId,
            Nombre = "Negocio Auth",
            Slug = NegocioSlug,
            Activo = true,
            Plan = "basic",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Users.Add(new User
        {
            Id = UserId,
            Email = Email,
            Password = hasher.Hash(Password),
            Nombre = "Admin Auth",
            Role = Role.ADMIN,
            Activo = true,
            NegocioId = NegocioId,
            EmailVerificado = true,
            CreatedAt = now,
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
    public async Task Flujo_completo_login_me_refresh_logout()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        });

        // 1. LOGIN ───────────────────────────────────────────────────────────
        var loginResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            email = Email,
            password = Password,
            negocioSlug = NegocioSlug,
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var setCookies = loginResponse.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith("access_token=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(setCookies, c => c.StartsWith("refresh_token=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        // 2. REQUEST AUTENTICADA (/auth/me) ──────────────────────────────────
        var meResponse = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(UserId, me!.Id);
        Assert.Equal(Email, me.Email);
        Assert.Equal("Admin Auth", me.Nombre);
        Assert.Equal("ADMIN", me.Role);
        Assert.Equal(NegocioId, me.NegocioId);

        // 3. REFRESH (rota el refresh token) ─────────────────────────────────
        var refreshResponse = await client.PostAsync("/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        await using (var assertScope = NewScope(out var db))
        {
            var tokens = await db.RefreshTokens.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, tokens.Count);                       // el original + el rotado
            Assert.Equal(1, tokens.Count(t => t.Revocado));      // el original quedó revocado
            Assert.Equal(1, tokens.Count(t => !t.Revocado));     // el nuevo, activo
        }

        // La cookie de access rotada sigue autenticando.
        var meAfterRefresh = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, meAfterRefresh.StatusCode);

        // 4. LOGOUT ──────────────────────────────────────────────────────────
        var logoutResponse = await client.PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        await using (var assertScope = NewScope(out var db))
        {
            var tokens = await db.RefreshTokens.IgnoreQueryFilters().ToListAsync();
            Assert.All(tokens, t => Assert.True(t.Revocado)); // todos revocados tras logout
        }

        // Tras logout las cookies se limpiaron: /auth/me queda sin autenticar.
        var meAfterLogout = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterLogout.StatusCode);
    }

    private AsyncServiceScope NewScope(out OrbitDbContext db)
    {
        var scope = _factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return scope;
    }

    private sealed record MeResponse(string Id, string Email, string Nombre, string Role, string? NegocioId);
}
