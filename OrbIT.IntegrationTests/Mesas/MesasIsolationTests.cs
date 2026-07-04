using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

using System.Text.Json.Serialization;

namespace OrbIT.IntegrationTests.Mesas;

/// <summary>
/// Test de integración end-to-end del <c>MesasController</c> real (in-memory) contra una base PostgreSQL
/// dedicada. Cubre:
/// <list type="bullet">
///   <item>aislamiento multi-tenant + unicidad de número por negocio (409) y reutilización entre negocios;</item>
///   <item>grilla del salón (config en 1 query, default 4×3, update, validación de posición);</item>
///   <item>ciclo de estado (ocupar exige pedidoActivoId, liberar) + hardening de cuenta abierta (400/409);</item>
///   <item>baja lógica (DELETE = activa=false, guard de mesa ocupada, sale del tablero pero sigue accesible);</item>
///   <item>projection liviana del tablero/detalle (conteo de ítems correlacionado).</item>
/// </list>
/// </summary>
[Collection(MesaApiCollection.Name)]
public sealed class MesasIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-me-a";
    private const string NegocioBId = "neg-me-b";
    private const string NegocioASlug = "negocio-me-a";
    private const string NegocioBSlug = "negocio-me-b";
    private const string AdminAId = "user-me-a";
    private const string AdminBId = "user-me-b";
    private const string AdminAEmail = "admin-a@me.test";
    private const string AdminBEmail = "admin-b@me.test";

    private readonly MesaWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        PlanSeed.Seed(db, now);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio ME A", NegocioASlug, AdminAId, AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio ME B", NegocioBSlug, AdminBId, AdminBEmail, now);
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
    public async Task Aislamiento_multitenant_y_unicidad_de_numero()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var mesaA = await CrearAsync(clientA, new { numero = 1, nombre = "Ventana" });
        Assert.Equal(EstadoMesa.LIBRE, mesaA.Estado);
        Assert.Equal(4, mesaA.Capacidad); // default DB
        Assert.True(mesaA.Activa);

        var mesaB = await CrearAsync(clientB, new { numero = 1 });

        // A sólo ve las suyas.
        var tableroA = await TableroAsync(clientA);
        Assert.Single(tableroA);
        Assert.Equal(mesaA.Id, tableroA[0].Id);

        // A no puede leer la de B por id → 404.
        var cross = await clientA.GetAsync($"/mesas/{mesaB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // Número duplicado dentro del negocio → 409.
        var dup = await clientA.PostAsJsonAsync("/mesas", new { numero = 1 });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // B sí puede usar el número 1 (otro negocio) — ya lo creamos arriba.
        var tableroB = await TableroAsync(clientB);
        Assert.Single(tableroB);
    }

    [Fact]
    public async Task Grilla_config_y_validacion_de_posicion()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        // Default 4×3.
        var grid = await clientA.GetFromJsonAsync<GridDto>("/mesas/config");
        Assert.Equal(4, grid!.Cols);
        Assert.Equal(3, grid.Rows);

        // Update a 6×5 (idempotente: dos updates seguidos no rompen).
        var upd = await clientA.PatchAsJsonAsync("/mesas/config", new { cols = 6, rows = 5 });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        await clientA.PatchAsJsonAsync("/mesas/config", new { cols = 6, rows = 5 });
        var grid2 = await clientA.GetFromJsonAsync<GridDto>("/mesas/config");
        Assert.Equal(6, grid2!.Cols);
        Assert.Equal(5, grid2.Rows);

        // posX fuera de la grilla (6 columnas → válido 0..5) → 400.
        var fuera = await clientA.PostAsJsonAsync("/mesas", new { numero = 10, posX = 6, posY = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, fuera.StatusCode);

        // Posición válida → 201.
        var ok = await CrearAsync(clientA, new { numero = 11, posX = 5, posY = 4 });
        Assert.Equal(5, ok.PosX);
        Assert.Equal(4, ok.PosY);
    }

    [Fact]
    public async Task Estado_ocupar_liberar_y_hardening_de_cuenta_abierta()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var m1 = await CrearAsync(clientA, new { numero = 1 });
        var m2 = await CrearAsync(clientA, new { numero = 2 });
        await SeedPedidoAsync("ped-1", NegocioAId);
        await SeedPedidoAsync("ped-2", NegocioAId);

        // OCUPADA sin pedidoActivoId → 400.
        var sinPedido = await clientA.PatchAsJsonAsync($"/mesas/{m1.Id}/estado", new { estado = EstadoMesa.OCUPADA });
        Assert.Equal(HttpStatusCode.BadRequest, sinPedido.StatusCode);

        // Ocupar m1 con ped-1 → 200.
        var ocupar = await clientA.PatchAsJsonAsync($"/mesas/{m1.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-1" });
        Assert.Equal(HttpStatusCode.OK, ocupar.StatusCode);
        var m1Ocupada = (await ocupar.Content.ReadFromJsonAsync<MesaDto>())!;
        Assert.Equal(EstadoMesa.OCUPADA, m1Ocupada.Estado);
        Assert.Equal("ped-1", m1Ocupada.PedidoActivoId);

        // Pasar a LIBRE con pedido activo presente → 400 (hay que liberar).
        var aLibre = await clientA.PatchAsJsonAsync($"/mesas/{m1.Id}/estado", new { estado = EstadoMesa.LIBRE });
        Assert.Equal(HttpStatusCode.BadRequest, aLibre.StatusCode);

        // Hardening: ocupar m1 (que ya tiene ped-1) con OTRO pedido → 400 "cuenta abierta".
        var pisar = await clientA.PatchAsJsonAsync($"/mesas/{m1.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-2" });
        Assert.Equal(HttpStatusCode.BadRequest, pisar.StatusCode);

        // Hardening: ocupar m2 con ped-1 (ya activo en m1) → 409 (índice UNIQUE de pedidoActivoId).
        var duplicado = await clientA.PatchAsJsonAsync($"/mesas/{m2.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-1" });
        Assert.Equal(HttpStatusCode.Conflict, duplicado.StatusCode);

        // Liberar m1 → 200, estado LIBRE, pedido nulo.
        var liberar = await clientA.PostAsJsonAsync($"/mesas/{m1.Id}/liberar", new { });
        Assert.Equal(HttpStatusCode.OK, liberar.StatusCode);
        var m1Libre = (await liberar.Content.ReadFromJsonAsync<MesaDto>())!;
        Assert.Equal(EstadoMesa.LIBRE, m1Libre.Estado);
        Assert.Null(m1Libre.PedidoActivoId);

        // Liberar de nuevo → 400 "ya está libre".
        var liberarOtra = await clientA.PostAsJsonAsync($"/mesas/{m1.Id}/liberar", new { });
        Assert.Equal(HttpStatusCode.BadRequest, liberarOtra.StatusCode);

        // Ahora m2 sí puede tomar ped-1 (ya libre en m1) → 200.
        var m2Ocupa = await clientA.PatchAsJsonAsync($"/mesas/{m2.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-1" });
        Assert.Equal(HttpStatusCode.OK, m2Ocupa.StatusCode);
    }

    [Fact]
    public async Task Delete_baja_logica_y_guard_de_mesa_ocupada()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var mesa = await CrearAsync(clientA, new { numero = 7 });
        await SeedPedidoAsync("ped-del", NegocioAId);

        await clientA.PatchAsJsonAsync($"/mesas/{mesa.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-del" });

        // Mesa ocupada → no se puede dar de baja (400).
        var bloqueado = await clientA.DeleteAsync($"/mesas/{mesa.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, bloqueado.StatusCode);

        // Liberar y dar de baja → 200, activa=false.
        await clientA.PostAsJsonAsync($"/mesas/{mesa.Id}/liberar", new { });
        var baja = await clientA.DeleteAsync($"/mesas/{mesa.Id}");
        Assert.Equal(HttpStatusCode.OK, baja.StatusCode);
        var mesaBaja = (await baja.Content.ReadFromJsonAsync<MesaDto>())!;
        Assert.False(mesaBaja.Activa);

        // Sale del tablero (que filtra activas)...
        var tablero = await TableroAsync(clientA);
        Assert.DoesNotContain(tablero, m => m.Id == mesa.Id);

        // ...pero el detalle por id sigue accesible (baja lógica, no borrado físico).
        var detalle = await clientA.GetAsync($"/mesas/{mesa.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
    }

    [Fact]
    public async Task Tablero_y_detalle_proyectan_pedido_activo_con_items()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var mesa = await CrearAsync(clientA, new { numero = 3 });
        await SeedProductoAsync("prod-1", "Pizza Muzza", NegocioAId);
        await SeedPedidoAsync("ped-items", NegocioAId, detalles: 2, productoId: "prod-1");

        await clientA.PatchAsJsonAsync($"/mesas/{mesa.Id}/estado",
            new { estado = EstadoMesa.OCUPADA, pedidoActivoId = "ped-items" });

        // Tablero: resumen con conteo de ítems (proyección correlacionada, sin traer los detalles).
        var tablero = await TableroAsync(clientA);
        var enTablero = Assert.Single(tablero, m => m.Id == mesa.Id);
        Assert.NotNull(enTablero.PedidoActivo);
        Assert.Equal("ped-items", enTablero.PedidoActivo!.Id);
        Assert.Equal(2, enTablero.PedidoActivo.CantidadItems);

        // Detalle: los ítems con nombre de producto.
        var detalle = await clientA.GetFromJsonAsync<MesaDetalleDto>($"/mesas/{mesa.Id}");
        Assert.NotNull(detalle!.PedidoActivo);
        Assert.Equal(2, detalle.PedidoActivo!.Detalles.Count);
        Assert.Equal("Pizza Muzza", detalle.PedidoActivo.Detalles[0].NombreProducto);
    }

    // ── Helpers HTTP ──────────────────────────────────────────────────────────────────────────

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

    private Task SeedProductoAsync(string id, string nombre, string negocioId) =>
        UsingDbAsync(async db =>
        {
            db.Productos.Add(new Producto { Id = id, Nombre = nombre, Precio = 500, NegocioId = negocioId });
            await db.SaveChangesAsync();
        });

    private Task SeedPedidoAsync(string pedidoId, string negocioId, int detalles = 0, string? productoId = null) =>
        UsingDbAsync(async db =>
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            db.Pedidos.Add(new Pedido
            {
                Id = pedidoId,
                Tipo = TipoPedido.LOCAL,
                Estado = EstadoPedido.EN_PREPARACION,
                EstadoPago = EstadoPago.PAGADO,
                Total = 1000,
                NegocioId = negocioId,
                CreatedAt = now,
            });
            for (var i = 0; i < detalles; i++)
            {
                db.PedidoDetalles.Add(new PedidoDetalle
                {
                    Id = $"{pedidoId}-d{i}",
                    PedidoId = pedidoId,
                    ProductoId = productoId!,
                    Cantidad = 1,
                    Subtotal = 500,
                    PrecioUnitario = 500,
                    NegocioId = negocioId,
                });
            }
            await db.SaveChangesAsync();
        });

    private static async Task<MesaDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/mesas", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MesaDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<MesaTableroDto[]> TableroAsync(HttpClient client)
    {
        var response = await client.GetAsync("/mesas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dtos = await response.Content.ReadFromJsonAsync<MesaTableroDto[]>();
        Assert.NotNull(dtos);
        return dtos!;
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
            Plan = "pro",
            PlanId = PlanSeed.ProId, // gestión de mesas es feature Pro-only.
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

    private sealed record MesaDto(
        string Id,
        int Numero,
        string? Nombre,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] EstadoMesa Estado,
        int Capacidad,
        bool Activa,
        int PosX,
        int PosY,
        string? PedidoActivoId);

    private sealed record MesaTableroDto(
        string Id, int Numero,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] EstadoMesa Estado,
        bool Activa, PedidoActivoResumenDto? PedidoActivo);

    private sealed record PedidoActivoResumenDto(string Id, double Total, int CantidadItems);

    private sealed record MesaDetalleDto(string Id, int Numero, bool Activa, PedidoActivoDetalleDto? PedidoActivo);

    private sealed record PedidoActivoDetalleDto(string Id, double Total, List<PedidoActivoItemDto> Detalles);

    private sealed record PedidoActivoItemDto(string Id, int Cantidad, double Subtotal, string? NombreProducto);

    private sealed record GridDto(int Cols, int Rows);
}
