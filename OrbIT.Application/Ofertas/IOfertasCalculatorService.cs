namespace OrbIT.Application.Ofertas;

/// <summary>Una línea de carrito para calcular ofertas. Los extras NO entran en el cálculo de ofertas
/// (el NestJS tampoco los usa); solo se necesita producto, cantidad, precio y si es pizza mitad-y-mitad.</summary>
public sealed record LineaCalculo(string ProductoId, int Cantidad, double PrecioUnitario, bool MediaMedia = false);

/// <summary>Una línea afectada por una oferta (para repartir el descuento, p.ej. en 2x1).</summary>
public sealed record LineaAfectada(int DetalleIdx, double DescuentoLinea);

/// <summary>La oferta que ganó (la de mayor descuento) y cómo se aplicó.</summary>
public sealed record OfertaAplicada(
    string OfertaId,
    string Nombre,
    string Tipo,
    double Descuento,
    IReadOnlyList<LineaAfectada> LineasAfectadas);

/// <summary>Resultado del cálculo: subtotal, descuento y total, más la oferta aplicada (0 o 1).</summary>
public sealed record ResultadoCalculo(
    double Subtotal,
    double Descuento,
    double Total,
    IReadOnlyList<OfertaAplicada> OfertasAplicadas);

/// <summary>
/// Calculadora de ofertas reutilizable (la consume el endpoint público <c>POST /ofertas/calcular</c> y,
/// más adelante, <c>crearPedido</c> del PedidosController). Vive en OrbIT.Application igual que
/// <c>IAuditLogService</c>: inyectable, scoped, comparte el <c>OrbitDbContext</c> del request.
///
/// Réplica funcional del <c>OfertasCalculatorService.calcularTotal</c> de NestJS, con las mejoras
/// acordadas: el grafo de ofertas NO trae las entidades <c>Producto</c> anidadas (solo se necesitan
/// productoId/precioEspecial/cantidad de grupo), y el mapa de precios se scopea por tenant gracias al
/// Global Query Filter (sin filtro manual).
/// </summary>
public interface IOfertasCalculatorService
{
    /// <summary>Calcula subtotal/descuento/total aplicando la mejor oferta vigente para el negocio.</summary>
    Task<ResultadoCalculo> CalcularAsync(
        IReadOnlyList<LineaCalculo> lineas,
        string negocioId,
        CancellationToken cancellationToken = default);

    /// <summary>Incrementa <c>usosActuales</c> de una oferta (lo llama el PedidosController al confirmar el pedido).</summary>
    Task IncrementarUsoAsync(string ofertaId, CancellationToken cancellationToken = default);
}
