using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Clientes;

/// <summary>
/// Test de integración end-to-end del <c>ClientesController</c> real (in-memory) contra una base
/// PostgreSQL dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant (auth por cookie → claim negocioId → query filter);</item>
///   <item>unicidad de teléfono por negocio (409) y reutilización entre negocios;</item>
///   <item>campos calculados del DTO (<c>ticketPromedio</c>, <c>esClienteFrecuente</c>);</item>
///   <item>upsert idempotente (mismo teléfono = un solo cliente) con acumulado de pedido por <c>montoPedido</c>;</item>
///   <item>historial de pedidos paginado server-side y preview embebido en el detalle;</item>
///   <item>guard de borrado con pedidos asociados (400) y borrado exitoso (204).</item>
/// </list>
/// </summary>
[Collection(ClienteApiCollection.Name)]
public sealed class ClientesIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-cl-a";
    private const string NegocioBId = "neg-cl-b";
    private const string NegocioASlug = "negocio-cl-a";
    private const string NegocioBSlug = "negocio-cl-b";
    private const string AdminAId = "user-cl-a";
    private const string AdminBId = "user-cl-b";
    private const string AdminAEmail = "admin-a@cl.test";
    private const string AdminBEmail = "admin-b@cl.test";

    private readonly ClienteWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio CL A", NegocioASlug, AdminAId, AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio CL B", NegocioBSlug, AdminBId, AdminBEmail, now);
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
    public async Task Aislamiento_multitenant_y_unicidad_de_telefono()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var anaA = await CrearAsync(clientA, new { nombre = "Ana", apellido = "Gómez", telefono = "111" });
        Assert.Equal(0, anaA.TotalPedidos);
        Assert.Equal(0, anaA.TicketPromedio);
        Assert.False(anaA.EsClienteFrecuente);

        var luisB = await CrearAsync(clientB, new { nombre = "Luis", telefono = "111" });

        // A sólo ve los suyos.
        var pageA = await ListarAsync(clientA);
        Assert.Equal(1, pageA.Total);
        Assert.Single(pageA.Data);
        Assert.Equal(anaA.Id, pageA.Data[0].Id);

        // A no puede leer el de B por id → 404 (no 403).
        var cross = await clientA.GetAsync($"/clientes/{luisB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Lookup por teléfono también scopeado: A ve a Ana, no al Luis de B (mismo teléfono).
        var lookup = await clientA.GetFromJsonAsync<ClienteDto>("/clientes/telefono/111");
        Assert.Equal(anaA.Id, lookup!.Id);

        // Unicidad de teléfono por negocio: A no puede repetir "111" → 409.
        var dup = await clientA.PostAsJsonAsync("/clientes", new { nombre = "Otra", telefono = "111" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // B sí puede tener "111" (otro negocio, otra unicidad) — ya lo creamos arriba.
        var pageB = await ListarAsync(clientB);
        Assert.Equal(1, pageB.Total);
    }

    [Fact]
    public async Task Upsert_es_idempotente_y_acumula_pedido_con_monto()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // Primer upsert sin monto (alta manual): crea el cliente con totales en cero.
        var primero = await UpsertAsync(clientA, new { nombre = "Caro", telefono = "222" });
        Assert.Equal(0, primero.TotalPedidos);
        Assert.Equal(0, primero.TotalGastado);
        Assert.Null(primero.FechaUltimoPedido);

        // Segundo upsert con monto (flujo de pedidos): NO duplica, acumula un pedido.
        var segundo = await UpsertAsync(clientA, new { nombre = "Caro", apellido = "Pérez", telefono = "222", montoPedido = 1500.0 });
        Assert.Equal(primero.Id, segundo.Id);
        Assert.Equal(1, segundo.TotalPedidos);
        Assert.Equal(1500.0, segundo.TotalGastado);
        Assert.Equal(1500.0, segundo.TicketPromedio);
        Assert.Equal("Pérez", segundo.Apellido);
        Assert.NotNull(segundo.FechaUltimoPedido);

        // Tercer upsert con otro monto: sigue siendo el mismo cliente, totales acumulan.
        var tercero = await UpsertAsync(clientA, new { nombre = "Caro", telefono = "222", montoPedido = 500.0 });
        Assert.Equal(primero.Id, tercero.Id);
        Assert.Equal(2, tercero.TotalPedidos);
        Assert.Equal(2000.0, tercero.TotalGastado);
        Assert.Equal(1000.0, tercero.TicketPromedio);

        // Un solo cliente en la base pese a los 3 upserts.
        var page = await ListarAsync(clientA);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Detalle_con_preview_e_historial_paginado()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var cliente = await CrearAsync(clientA, new { nombre = "Frecuente", telefono = "333" });

        // Seed de 25 pedidos para este cliente (createdAt creciente → el más nuevo primero en el orden).
        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 6, 1, 10, 0, 0), DateTimeKind.Unspecified);
        await UsingDbAsync(async db =>
        {
            for (var i = 0; i < 25; i++)
            {
                db.Pedidos.Add(new Pedido
                {
                    Id = $"ped-{i:D2}",
                    Tipo = TipoPedido.DELIVERY,
                    Estado = EstadoPedido.ENTREGADO,
                    EstadoPago = EstadoPago.PAGADO,
                    Total = 100 + i,
                    Direccion = $"Calle {i}",
                    ClienteId = cliente.Id,
                    NegocioId = NegocioAId,
                    CreatedAt = baseTime.AddMinutes(i),
                });
            }
            await db.SaveChangesAsync();
        });

        // Detalle: preview de 20 pedidos, el más reciente primero.
        var detalle = await clientA.GetFromJsonAsync<ClienteDetalleDto>($"/clientes/{cliente.Id}");
        Assert.Equal(20, detalle!.PedidosRecientes.Count);
        Assert.Equal("ped-24", detalle.PedidosRecientes[0].Id);
        Assert.Equal(EstadoPedido.ENTREGADO, detalle.PedidosRecientes[0].Estado);

        // Historial paginado: page 1, limit 10 → 10 items, total 25.
        var p1 = await clientA.GetFromJsonAsync<PedidosPageDto>($"/clientes/{cliente.Id}/pedidos?page=1&limit=10");
        Assert.Equal(25, p1!.Total);
        Assert.Equal(10, p1.Data.Count);
        Assert.Equal("ped-24", p1.Data[0].Id);

        // Última página: 5 restantes.
        var p3 = await clientA.GetFromJsonAsync<PedidosPageDto>($"/clientes/{cliente.Id}/pedidos?page=3&limit=10");
        Assert.Equal(5, p3!.Data.Count);
        Assert.Equal("ped-04", p3.Data[0].Id);  // página 3 (desc): ped-04 .. ped-00
        Assert.Equal("ped-00", p3.Data[^1].Id);

        // Historial de un cliente inexistente / ajeno → 404.
        var ajeno = await clientA.GetAsync("/clientes/no-existe/pedidos");
        Assert.Equal(HttpStatusCode.NotFound, ajeno.StatusCode);
    }

    [Fact]
    public async Task Borrado_con_pedidos_falla_y_sin_pedidos_borra()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var conPedidos = await CrearAsync(clientA, new { nombre = "Con Pedidos", telefono = "444" });
        var sinPedidos = await CrearAsync(clientA, new { nombre = "Sin Pedidos", telefono = "555" });

        await UsingDbAsync(async db =>
        {
            db.Pedidos.Add(new Pedido
            {
                Id = "ped-guard",
                Tipo = TipoPedido.RETIRO,
                Estado = EstadoPedido.ENTREGADO,
                EstadoPago = EstadoPago.PAGADO,
                Total = 999,
                ClienteId = conPedidos.Id,
                NegocioId = NegocioAId,
                CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            });
            await db.SaveChangesAsync();
        });

        // Con pedidos → 400 (guard aplicativo: la FK es SET NULL, la DB no bloquea).
        var bloqueado = await clientA.DeleteAsync($"/clientes/{conPedidos.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, bloqueado.StatusCode);

        // Sin pedidos → 204.
        var borrado = await clientA.DeleteAsync($"/clientes/{sinPedidos.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrado.StatusCode);

        var yaNoEsta = await clientA.GetAsync($"/clientes/{sinPedidos.Id}");
        Assert.Equal(HttpStatusCode.NotFound, yaNoEsta.StatusCode);
    }

    [Fact]
    public async Task Stats_cuenta_total_y_clientes_con_mas_de_un_pedido()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // Cliente sin pedidos, con 1 pedido y con 3 pedidos (vía upsert con monto).
        await CrearAsync(clientA, new { nombre = "Cero", telefono = "601" });
        await UpsertAsync(clientA, new { nombre = "Uno", telefono = "602", montoPedido = 100.0 });
        await UpsertAsync(clientA, new { nombre = "Tres", telefono = "603", montoPedido = 100.0 });
        await UpsertAsync(clientA, new { nombre = "Tres", telefono = "603", montoPedido = 100.0 });
        await UpsertAsync(clientA, new { nombre = "Tres", telefono = "603", montoPedido = 100.0 });

        var stats = await clientA.GetFromJsonAsync<StatsDto>("/clientes/stats");
        Assert.Equal(3, stats!.Total);
        Assert.Equal(1, stats.ConMasDeUnPedido); // sólo "Tres" tiene > 1 pedido
        Assert.Equal("Tres", stats.TopClientes[0].Nombre); // ordenado por totalPedidos desc
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> LoginAsync(string email, string negocioSlug)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
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

    private static async Task<ClienteDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/clientes", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ClienteDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ClienteDto> UpsertAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/clientes/upsert", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ClienteDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<ClientesPageDto> ListarAsync(HttpClient client)
    {
        var response = await client.GetAsync("/clientes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ClientesPageDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static void SeedNegocioConAdmin(
        OrbitDbContext db, IPasswordHasher hasher,
        string negocioId, string negocioNombre, string slug,
        string userId, string email, DateTime now)
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
    }

    private sealed record ClienteDto(
        string Id,
        string Nombre,
        string? Apellido,
        string Telefono,
        int TotalPedidos,
        double TotalGastado,
        double TicketPromedio,
        bool EsClienteFrecuente,
        DateTime? FechaUltimoPedido);

    private sealed record ClienteDetalleDto(
        string Id,
        string Nombre,
        List<PedidoPreviewDto> PedidosRecientes);

    private sealed record PedidoPreviewDto(string Id, EstadoPedido Estado, double Total, string? Direccion);

    private sealed record ClientesPageDto(List<ClienteDto> Data, int Total);

    private sealed record PedidosPageDto(List<PedidoPreviewDto> Data, int Total);

    private sealed record StatsDto(int Total, int ConMasDeUnPedido, List<ClienteDto> TopClientes);
}
