using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests.Ofertas;

/// <summary>
/// Test de integración end-to-end del <c>OfertasController</c> + <c>OfertasCalculatorService</c> reales.
/// Cubre aislamiento multi-tenant, validación de productos cross-tenant, los 3 tipos de oferta más usados
/// en la calculadora (2x1, COMBO completo/incompleto, %), el límite de usos, el listado público por slug y
/// el guard de borrado con histórico de pedidos.
/// </summary>
[Collection(OfertaApiCollection.Name)]
public sealed class OfertasIsolationTests : IAsyncLifetime
{
    private const string Password = "secret-password-123";

    private const string NegocioAId = "neg-of-a";
    private const string NegocioBId = "neg-of-b";
    private const string NegocioASlug = "negocio-of-a";
    private const string NegocioBSlug = "negocio-of-b";
    private const string AdminAEmail = "admin-a@of.test";
    private const string AdminBEmail = "admin-b@of.test";

    private const string PizzaId = "prod-of-pizza";   // negocio A, precio 200
    private const string EmpanadaId = "prod-of-emp";   // negocio A, precio 100
    private const string ProductoBId = "prod-of-b";    // negocio B

    private readonly OfertaWebAppFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        PlanSeed.Seed(db, now);
        SeedNegocioConAdmin(db, hasher, NegocioAId, "Negocio OF A", NegocioASlug, "user-of-a", AdminAEmail, now);
        SeedNegocioConAdmin(db, hasher, NegocioBId, "Negocio OF B", NegocioBSlug, "user-of-b", AdminBEmail, now);

        db.Productos.Add(new Producto { Id = PizzaId, Nombre = "Pizza", Precio = 200, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = EmpanadaId, Nombre = "Empanada", Precio = 100, NegocioId = NegocioAId });
        db.Productos.Add(new Producto { Id = ProductoBId, Nombre = "Producto B", Precio = 50, NegocioId = NegocioBId });
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
    public async Task Aislamiento_multitenant_y_productos_cross_tenant()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        var clientB = await LoginAsync(AdminBEmail, NegocioBSlug);

        var ofertaA = await CrearAsync(clientA, new
        {
            nombre = "2x1 Pizza",
            tipo = TipoOferta.DOS_POR_UNO,
            fechaInicio = "2020-01-01",
            productos = new[] { new { productoId = PizzaId } },
        });

        // B no ve la oferta de A.
        var listaB = await clientB.GetFromJsonAsync<OfertaDto[]>("/ofertas");
        Assert.DoesNotContain(listaB!, o => o.Id == ofertaA.Id);

        // A no puede leer la de... (cross-tenant GET de un id ajeno → 404).
        var cross = await clientB.GetAsync($"/ofertas/{ofertaA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        // A no puede crear una oferta apuntando a un producto de B → 400 (validación estructural de tenant).
        var conProductoAjeno = await clientA.PostAsJsonAsync("/ofertas", new
        {
            nombre = "Trucha",
            tipo = TipoOferta.DOS_POR_UNO,
            fechaInicio = "2020-01-01",
            productos = new[] { new { productoId = ProductoBId } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, conProductoAjeno.StatusCode);
    }

    [Fact]
    public async Task Listado_publico_por_slug()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);
        await CrearAsync(clientA, new
        {
            nombre = "Promo pública",
            tipo = TipoOferta.DESCUENTO_PORCENTAJE,
            fechaInicio = "2020-01-01",
            porcentajeDescuento = 10,
        });

        // Cliente anónimo (sin login) con ?negocio=slug.
        var anon = _factory.CreateClient();
        var lista = await anon.GetFromJsonAsync<OfertaDto[]>($"/ofertas?negocio={NegocioASlug}");
        Assert.Single(lista!);
        Assert.Equal("Promo pública", lista![0].Nombre);

        // Sin slug ni sesión → 400 (no se puede resolver el tenant).
        var sinTenant = await anon.GetAsync("/ofertas");
        Assert.Equal(HttpStatusCode.BadRequest, sinTenant.StatusCode);
    }

    [Fact]
    public async Task Calc_dos_por_uno_y_limite_de_usos()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var oferta = await CrearAsync(clientA, new
        {
            nombre = "2x1 Pizza",
            tipo = TipoOferta.DOS_POR_UNO,
            fechaInicio = "2020-01-01",
            maxUsosTotales = 1,
            productos = new[] { new { productoId = PizzaId } },
        });

        // 2 pizzas (precio 200 c/u del map): subtotal 400, descuento 200 (la 2da gratis), total 200.
        var calc = await CalcularAsync(clientA, new object[] { new { productoId = PizzaId, cantidad = 2 } });
        Assert.Equal(400, calc.Subtotal);
        Assert.Equal(200, calc.Descuento);
        Assert.Equal(200, calc.Total);
        Assert.Single(calc.OfertasAplicadas);
        Assert.Equal("DOS_POR_UNO", calc.OfertasAplicadas[0].Tipo);

        // Agotamos los usos (usosActuales = maxUsosTotales) → la oferta deja de aplicar.
        await UsingDbAsync(async db =>
        {
            await db.Oferta.IgnoreQueryFilters().Where(o => o.Id == oferta.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.UsosActuales, 1));
        });

        var calcAgotada = await CalcularAsync(clientA, new object[] { new { productoId = PizzaId, cantidad = 2 } });
        Assert.Equal(0, calcAgotada.Descuento);
        Assert.Equal(400, calcAgotada.Total);
        Assert.Empty(calcAgotada.OfertasAplicadas);
    }

    [Fact]
    public async Task Calc_combo_completo_e_incompleto()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        await CrearAsync(clientA, new
        {
            nombre = "Combo Pizza",
            tipo = TipoOferta.COMBO,
            fechaInicio = "2020-01-01",
            montoDescuento = 150, // precio del combo
            gruposCombo = new[]
            {
                new
                {
                    nombre = "Principal",
                    obligatorio = true,
                    cantidad = 1,
                    opciones = new[] { new { productoId = PizzaId } },
                },
            },
        });

        // Combo completo: 1 pizza (precio 200) → precioIndividual 200, precioCombo 150, descuento 50.
        var completo = await CalcularAsync(clientA, new object[] { new { productoId = PizzaId, cantidad = 1 } });
        Assert.Equal(50, completo.Descuento);
        Assert.Equal(150, completo.Total);

        // Combo incompleto: sin pizza (solo empanada) → no se cumple el grupo obligatorio → sin descuento.
        var incompleto = await CalcularAsync(clientA, new object[] { new { productoId = EmpanadaId, cantidad = 1 } });
        Assert.Equal(0, incompleto.Descuento);
        Assert.Empty(incompleto.OfertasAplicadas);
    }

    [Fact]
    public async Task Calc_descuento_porcentaje()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        await CrearAsync(clientA, new
        {
            nombre = "10% OFF",
            tipo = TipoOferta.DESCUENTO_PORCENTAJE,
            fechaInicio = "2020-01-01",
            porcentajeDescuento = 10,
        });

        // El % usa el precioUnitario de la línea: 2 × 100 = 200 → 10% = 20.
        var calc = await CalcularAsync(clientA, new object[]
        {
            new { productoId = PizzaId, cantidad = 2, precioUnitario = 100 },
        });
        Assert.Equal(20, calc.Descuento);
        Assert.Equal(180, calc.Total);
        Assert.Equal("DESCUENTO_PORCENTAJE", calc.OfertasAplicadas[0].Tipo);
    }

    [Fact]
    public async Task Delete_con_historico_409_y_sin_historico_204()
    {
        var clientA = await LoginAsync(AdminAEmail, NegocioASlug);

        var conHistorial = await CrearAsync(clientA, new
        {
            nombre = "Con historial",
            tipo = TipoOferta.DESCUENTO_MONTO_FIJO,
            fechaInicio = "2020-01-01",
            montoDescuento = 50,
        });
        var sinHistorial = await CrearAsync(clientA, new
        {
            nombre = "Sin historial",
            tipo = TipoOferta.DESCUENTO_MONTO_FIJO,
            fechaInicio = "2020-01-01",
            montoDescuento = 50,
        });

        // Seed de un pedido + pedidoOferta que referencia "conHistorial".
        await UsingDbAsync(async db =>
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            db.Pedidos.Add(new Pedido
            {
                Id = "ped-of-1",
                Tipo = TipoPedido.LOCAL,
                Estado = EstadoPedido.ENTREGADO,
                EstadoPago = EstadoPago.PAGADO,
                Total = 500,
                NegocioId = NegocioAId,
                CreatedAt = now,
            });
            db.PedidoOferta.Add(new PedidoOfertum
            {
                Id = "pedof-1",
                PedidoId = "ped-of-1",
                OfertaId = conHistorial.Id,
                PrecioOriginal = 550,
                PrecioFinal = 500,
                DescuentoAplicado = 50,
                NegocioId = NegocioAId,
            });
            await db.SaveChangesAsync();
        });

        // Con histórico → 409 (no se destruye el historial).
        var bloqueado = await clientA.DeleteAsync($"/ofertas/{conHistorial.Id}");
        Assert.Equal(HttpStatusCode.Conflict, bloqueado.StatusCode);

        // Sin histórico → 204 (y el CASCADE limpia productos/grupos).
        var borrado = await clientA.DeleteAsync($"/ofertas/{sinHistorial.Id}");
        Assert.Equal(HttpStatusCode.NoContent, borrado.StatusCode);
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

    private static async Task<OfertaDto> CrearAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/ofertas", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<OfertaDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static async Task<CalcResultDto> CalcularAsync(HttpClient client, object[] lineas)
    {
        var response = await client.PostAsJsonAsync("/ofertas/calcular", new { lineas });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CalcResultDto>();
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
            Plan = "pro",
            PlanId = PlanSeed.ProId, // ofertas es feature Pro-only.
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

    private sealed record OfertaDto(string Id, string Nombre, TipoOferta Tipo, bool Activa);

    private sealed record CalcResultDto(double Subtotal, double Descuento, double Total, List<OfertaAplicadaDto> OfertasAplicadas);

    private sealed record OfertaAplicadaDto(string OfertaId, string Nombre, string Tipo, double Descuento);
}
