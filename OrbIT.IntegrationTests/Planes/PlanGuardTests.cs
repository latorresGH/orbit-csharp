using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Planes;

/// <summary>
/// Tests end-to-end del enganche de <c>IPlanGuard</c> a los controllers + el nuevo <c>UsuariosController</c>.
/// Cubre los límites (productos, usuarios) y las features (mesas) resueltas por plan, incluyendo el fallback
/// fail-closed a Básico cuando el negocio no tiene <c>planId</c>.
/// </summary>
[Collection(PlanGuardApiCollection.Name)]
public sealed class PlanGuardTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    // Negocio Básico sin planId asignado (ejerce el fallback a 'basic').
    private const string NegBasicId = "pg-neg-basic";
    private const string NegBasicSlug = "pg-neg-basic";
    private const string AdminBasicEmail = "admin-basic@pg.test";
    private const string AdminBasicId = "pg-admin-basic";

    // Negocio Pro.
    private const string NegProId = "pg-neg-pro";
    private const string NegProSlug = "pg-neg-pro";
    private const string AdminProEmail = "admin-pro@pg.test";
    private const string AdminProId = "pg-admin-pro";

    // Negocio Básico con 30 productos (tope de productos alcanzado).
    private const string NegProdId = "pg-neg-prod";
    private const string NegProdSlug = "pg-neg-prod";
    private const string AdminProdEmail = "admin-prod@pg.test";
    private const string AdminProdId = "pg-admin-prod";
    private const string CategoriaProdId = "pg-cat-prod";

    // Negocio Básico con 5 usuarios activos (tope de usuarios alcanzado).
    private const string NegUsersId = "pg-neg-users";
    private const string NegUsersSlug = "pg-neg-users";
    private const string AdminUsersEmail = "admin-users@pg.test";
    private const string AdminUsersId = "pg-admin-users";

    private readonly PlanGuardWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        PlanSeed.Seed(db, now);

        // Básico SIN planId → PlanGuard cae al plan 'basic' (fail-closed). Un solo admin (hay lugar para más).
        db.Negocios.Add(Negocio(NegBasicId, "PG Básico", NegBasicSlug, planId: null, now));
        db.Users.Add(Admin(AdminBasicId, AdminBasicEmail, NegBasicId, hasher, now));

        // Pro.
        db.Negocios.Add(Negocio(NegProId, "PG Pro", NegProSlug, planId: PlanSeed.ProId, now));
        db.Users.Add(Admin(AdminProId, AdminProEmail, NegProId, hasher, now));

        // Básico con 30 productos activos (== límite) y una categoría válida.
        db.Negocios.Add(Negocio(NegProdId, "PG Prod", NegProdSlug, planId: PlanSeed.BasicId, now));
        db.Users.Add(Admin(AdminProdId, AdminProdEmail, NegProdId, hasher, now));
        db.Categoria.Add(new Categorium
        {
            Id = CategoriaProdId, Nombre = "Pizzas", Activo = true, Orden = 0,
            MaxAderezosGratis = 2, NegocioId = NegProdId, CreatedAt = now, UpdatedAt = now,
        });
        for (var i = 0; i < 30; i++)
        {
            db.Productos.Add(new Producto
            {
                Id = $"pg-prod-{i}", Nombre = $"Producto {i}", Precio = 100, Activo = true,
                CategoriaId = CategoriaProdId, NegocioId = NegProdId,
            });
        }

        // Básico con 5 usuarios activos (== límite): 1 admin + 4 empleados.
        db.Negocios.Add(Negocio(NegUsersId, "PG Users", NegUsersSlug, planId: PlanSeed.BasicId, now));
        db.Users.Add(Admin(AdminUsersId, AdminUsersEmail, NegUsersId, hasher, now));
        for (var i = 0; i < 4; i++)
        {
            db.Users.Add(new User
            {
                Id = $"pg-user-{i}", Email = $"emp{i}@pg-users.test", Password = hasher.Hash(Password),
                Nombre = $"Empleado {i}", Role = Role.TRABAJADOR, Activo = true, NegocioId = NegUsersId,
                EmailVerificado = true, CreatedAt = now,
            });
        }

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

    // ── Los 4 escenarios pedidos ───────────────────────────────────────────────

    [Fact]
    public async Task Negocio_sin_plan_tratado_como_basico_no_puede_crear_mesas()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var response = await client.PostAsJsonAsync("/mesas", new { numero = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Negocio_pro_puede_crear_mesas()
    {
        var client = await LoginAsync(AdminProEmail, NegProSlug);

        var response = await client.PostAsJsonAsync("/mesas", new { numero = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Basico_con_30_productos_no_puede_crear_mas()
    {
        var client = await LoginAsync(AdminProdEmail, NegProdSlug);

        var response = await client.PostAsJsonAsync("/productos",
            new { nombre = "Producto 31", precio = 100.0, categoriaId = CategoriaProdId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Basico_con_5_usuarios_no_puede_crear_mas()
    {
        var client = await LoginAsync(AdminUsersEmail, NegUsersSlug);

        var response = await client.PostAsJsonAsync("/usuarios",
            new { email = "nuevo@pg-users.test", password = Password, nombre = "Nuevo", rol = Role.TRABAJADOR });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── UsuariosController (funcional) ─────────────────────────────────────────

    [Fact]
    public async Task Admin_crea_empleado_y_aparece_en_la_lista()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var crear = await client.PostAsJsonAsync("/usuarios",
            new { email = "delivery@pg-basic.test", password = Password, nombre = "Repartidor", rol = Role.DELIVERY });
        Assert.Equal(HttpStatusCode.Created, crear.StatusCode);
        var creado = (await crear.Content.ReadFromJsonAsync<UsuarioDto>())!;
        Assert.Equal("DELIVERY", creado.Rol);
        Assert.True(creado.Activo);

        var lista = await client.GetFromJsonAsync<List<UsuarioDto>>("/usuarios");
        Assert.Contains(lista!, u => u.Id == creado.Id);
        Assert.Contains(lista!, u => u.Rol == "ADMIN"); // el admin sembrado
    }

    [Fact]
    public async Task No_se_puede_crear_un_usuario_con_rol_admin()
    {
        var client = await LoginAsync(AdminProEmail, NegProSlug);

        var response = await client.PostAsJsonAsync("/usuarios",
            new { email = "otro-admin@pg-pro.test", password = Password, nombre = "Otro Admin", rol = Role.ADMIN });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Email_duplicado_en_el_negocio_devuelve_409()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var response = await client.PostAsJsonAsync("/usuarios",
            new { email = AdminBasicEmail, password = Password, nombre = "Choca", rol = Role.TRABAJADOR });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Admin_no_puede_borrar_su_propia_cuenta()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var response = await client.DeleteAsync($"/usuarios/{AdminBasicId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_borra_un_empleado_creado()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var crear = await client.PostAsJsonAsync("/usuarios",
            new { email = "temp@pg-basic.test", password = Password, nombre = "Temporal", rol = Role.TRABAJADOR });
        var creado = (await crear.Content.ReadFromJsonAsync<UsuarioDto>())!;

        var borrar = await client.DeleteAsync($"/usuarios/{creado.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrar.StatusCode);

        var lista = await client.GetFromJsonAsync<List<UsuarioDto>>("/usuarios");
        Assert.DoesNotContain(lista!, u => u.Id == creado.Id);
    }

    [Fact]
    public async Task Admin_desactiva_un_empleado()
    {
        var client = await LoginAsync(AdminBasicEmail, NegBasicSlug);

        var crear = await client.PostAsJsonAsync("/usuarios",
            new { email = "toggle@pg-basic.test", password = Password, nombre = "Toggle", rol = Role.TRABAJADOR });
        var creado = (await crear.Content.ReadFromJsonAsync<UsuarioDto>())!;

        var desactivar = await client.PatchAsJsonAsync($"/usuarios/{creado.Id}/activo", new { activo = false });
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);
        var actualizado = (await desactivar.Content.ReadFromJsonAsync<UsuarioDto>())!;
        Assert.False(actualizado.Activo);
    }

    [Fact]
    public async Task Empleado_no_puede_acceder_a_la_gestion_de_usuarios()
    {
        // Un TRABAJADOR (rol operativo) no debe poder listar usuarios: el controller es ADMIN-only.
        var client = await LoginAsync("emp0@pg-users.test", NegUsersSlug);

        var response = await client.GetAsync("/usuarios");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password, negocioSlug });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Negocio Negocio(string id, string nombre, string slug, string? planId, DateTime now) => new()
    {
        Id = id,
        Nombre = nombre,
        Slug = slug,
        Activo = true,
        Plan = planId == PlanSeed.ProId ? "pro" : "basic",
        PlanId = planId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static User Admin(string id, string email, string negocioId, IPasswordHasher hasher, DateTime now) => new()
    {
        Id = id,
        Email = email,
        Password = hasher.Hash(Password),
        Nombre = "Admin",
        Role = Role.ADMIN,
        Activo = true,
        NegocioId = negocioId,
        EmailVerificado = true,
        CreatedAt = now,
    };

    private sealed record UsuarioDto(string Id, string Email, string Nombre, string Rol, bool Activo, DateTime CreatedAt);
}
