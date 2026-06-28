using Microsoft.EntityFrameworkCore;

namespace OrbIT.IntegrationTests;

/// <summary>
/// Verifica que los global query filters del <c>OrbitDbContext</c> aíslan las lecturas
/// por negocio (tenant) y que <c>IgnoreQueryFilters()</c> permite el acceso cross-negocio
/// de forma explícita.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MultiTenantQueryFilterTests
{
    private readonly DatabaseFixture _fixture;

    public MultiTenantQueryFilterTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Lectura_de_productos_se_aisla_al_negocio_activo()
    {
        await using var ctxA = _fixture.CreateContext(DatabaseFixture.NegocioAId);
        var productosA = await ctxA.Productos.ToListAsync();

        Assert.Equal(2, productosA.Count);
        Assert.All(productosA, p => Assert.Equal(DatabaseFixture.NegocioAId, p.NegocioId));

        await using var ctxB = _fixture.CreateContext(DatabaseFixture.NegocioBId);
        var productosB = await ctxB.Productos.ToListAsync();

        Assert.Single(productosB);
        Assert.All(productosB, p => Assert.Equal(DatabaseFixture.NegocioBId, p.NegocioId));
    }

    [Fact]
    public async Task Un_negocio_no_ve_datos_de_otro()
    {
        await using var ctxA = _fixture.CreateContext(DatabaseFixture.NegocioAId);

        // Ningún producto de B es visible bajo el tenant A.
        var verProductoDeB = await ctxA.Productos.AnyAsync(p => p.Id == "prod-b1");
        Assert.False(verProductoDeB);

        // Ningún cliente de B es visible bajo el tenant A.
        var clientesA = await ctxA.Clientes.ToListAsync();
        Assert.Single(clientesA);
        Assert.Equal(DatabaseFixture.NegocioAId, clientesA[0].NegocioId);
    }

    [Fact]
    public async Task Negocio_se_filtra_por_su_propia_Id()
    {
        await using var ctxA = _fixture.CreateContext(DatabaseFixture.NegocioAId);
        var negociosA = await ctxA.Negocios.ToListAsync();

        Assert.Single(negociosA);
        Assert.Equal(DatabaseFixture.NegocioAId, negociosA[0].Id);
    }

    [Fact]
    public async Task Sin_tenant_no_se_ve_nada()
    {
        await using var ctx = _fixture.CreateContext(negocioId: null);

        Assert.Empty(await ctx.Productos.ToListAsync());
        Assert.Empty(await ctx.Negocios.ToListAsync());
    }

    [Fact]
    public async Task IgnoreQueryFilters_devuelve_datos_de_todos_los_negocios()
    {
        await using var ctxA = _fixture.CreateContext(DatabaseFixture.NegocioAId);

        var todosLosProductos = await ctxA.Productos.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(3, todosLosProductos.Count);

        var todosLosNegocios = await ctxA.Negocios.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, todosLosNegocios.Count);

        var todosLosClientes = await ctxA.Clientes.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, todosLosClientes.Count);
    }
}
