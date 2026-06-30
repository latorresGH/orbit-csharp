using Microsoft.EntityFrameworkCore;
using OrbIT.Application.Common;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Ofertas;

/// <summary>
/// Implementación de <see cref="IOfertasCalculatorService"/> sobre el <see cref="OrbitDbContext"/>.
/// Dos queries acotadas (sin N+1): (1) ofertas vigentes con su grafo liviano —sin <c>Producto</c>—,
/// (2) precios de los productos del carrito (scopeados por tenant vía Global Query Filter). El resto del
/// cálculo es en memoria. Aplica <b>una sola</b> oferta: la de mayor descuento.
/// </summary>
public sealed class OfertasCalculatorService : IOfertasCalculatorService
{
    private readonly OrbitDbContext _db;

    public OfertasCalculatorService(OrbitDbContext db) => _db = db;

    public async Task<ResultadoCalculo> CalcularAsync(
        IReadOnlyList<LineaCalculo> lineas,
        string negocioId,
        CancellationToken cancellationToken = default)
    {
        var ahora = ArgentinaClock.Now();
        var diaSemana = ArgentinaClock.DiaSemana(ahora);
        var horaActual = ArgentinaClock.HoraHhMm(ahora);

        // Query 1: ofertas vigentes (por estado/fechas). El grafo trae OfertaProductos y GrupoCombos→
        // GrupoOpcions, pero NO las entidades Producto anidadas (no se usan en el cálculo). El negocio se
        // filtra explícito por el parámetro (además del Global Query Filter, que apunta al mismo tenant).
        var ofertas = await _db.Oferta.AsNoTracking()
            .Where(o => o.NegocioId == negocioId
                        && o.Activa
                        && o.Estado == EstadoOferta.ACTIVA
                        && o.FechaInicio <= ahora
                        && (o.FechaFin == null || o.FechaFin >= ahora))
            .Include(o => o.OfertaProductos)
            .Include(o => o.GrupoCombos).ThenInclude(g => g.GrupoOpcions)
            .ToListAsync(cancellationToken);

        // Filtro en memoria por día de semana y ventana horaria (strings, no traducibles a SQL).
        var ofertasVigentes = ofertas.Where(o => AplicaHoy(o, diaSemana, horaActual)).ToList();

        // Query 2: precios de los productos del carrito (Map). Scopeado por tenant (query filter) → un
        // productoId ajeno cae fuera del mapa y no aporta precio (más seguro que el NestJS sin filtro).
        var productoIds = lineas.Select(l => l.ProductoId).Distinct().ToList();
        var preciosMap = await _db.Productos.AsNoTracking()
            .Where(p => productoIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Precio, cancellationToken);

        var subtotal = 0.0;
        foreach (var linea in lineas)
        {
            var precioBase = linea.PrecioUnitario > 0
                ? linea.PrecioUnitario
                : (preciosMap.TryGetValue(linea.ProductoId, out var p) ? p : 0);
            subtotal += precioBase * linea.Cantidad;
        }

        return AplicarMejorOferta(lineas, subtotal, ofertasVigentes, preciosMap);
    }

    public Task IncrementarUsoAsync(string ofertaId, CancellationToken cancellationToken = default) =>
        _db.Oferta
            .Where(o => o.Id == ofertaId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UsosActuales, o => o.UsosActuales + 1), cancellationToken);

    // ─────────────────────────────────────────────────────────────────────────

    private static bool AplicaHoy(Ofertum oferta, int diaSemana, string horaActual)
    {
        var dias = oferta.DiasAplicables
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var n) ? n : -1);
        if (!dias.Contains(diaSemana))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(oferta.HoraInicio) && !string.IsNullOrEmpty(oferta.HoraFin))
        {
            // Comparación lexicográfica de "HH:mm" (igual que NestJS); válida porque el formato es fijo.
            if (string.CompareOrdinal(horaActual, oferta.HoraInicio) < 0 ||
                string.CompareOrdinal(horaActual, oferta.HoraFin) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static ResultadoCalculo AplicarMejorOferta(
        IReadOnlyList<LineaCalculo> lineas,
        double subtotal,
        IReadOnlyList<Ofertum> ofertas,
        IReadOnlyDictionary<string, double> precios)
    {
        var mejorDescuento = 0.0;
        OfertaAplicada? mejorOferta = null;

        foreach (var oferta in ofertas)
        {
            if (oferta.MaxUsosTotales is { } max && oferta.UsosActuales >= max)
            {
                continue;
            }

            var resultado = EvaluarOferta(oferta, lineas, subtotal, precios);
            if (resultado is not null && resultado.Descuento > mejorDescuento)
            {
                mejorDescuento = resultado.Descuento;
                mejorOferta = resultado;
            }
        }

        return new ResultadoCalculo(
            subtotal,
            mejorDescuento,
            Math.Max(0, subtotal - mejorDescuento),
            mejorOferta is null ? Array.Empty<OfertaAplicada>() : new[] { mejorOferta });
    }

    private static OfertaAplicada? EvaluarOferta(
        Ofertum oferta, IReadOnlyList<LineaCalculo> lineas, double subtotal, IReadOnlyDictionary<string, double> precios) =>
        oferta.Tipo switch
        {
            TipoOferta.DOS_POR_UNO => Evaluar2x1(oferta, lineas, precios),
            TipoOferta.COMBO => EvaluarCombo(oferta, lineas, precios),
            TipoOferta.DESCUENTO_PORCENTAJE => EvaluarDescuentoPorcentaje(oferta, lineas),
            TipoOferta.DESCUENTO_MONTO_FIJO => EvaluarDescuentoMontoFijo(oferta, lineas),
            _ => null,
        };

    private static OfertaAplicada? Evaluar2x1(
        Ofertum oferta, IReadOnlyList<LineaCalculo> lineas, IReadOnlyDictionary<string, double> precios)
    {
        var productosOferta = oferta.OfertaProductos.Select(p => p.ProductoId).ToHashSet();
        if (productosOferta.Count == 0)
        {
            return null;
        }

        // Expandir las líneas que matchean en unidades individuales (ignorando pizzas mitad-y-mitad).
        var unidades = new List<(double Precio, int LineaIdx)>();
        for (var li = 0; li < lineas.Count; li++)
        {
            var linea = lineas[li];
            if (linea.MediaMedia || !productosOferta.Contains(linea.ProductoId))
            {
                continue;
            }

            var precioBase = precios.TryGetValue(linea.ProductoId, out var p)
                ? p
                : (linea.PrecioUnitario > 0 ? linea.PrecioUnitario : 0);
            for (var u = 0; u < linea.Cantidad; u++)
            {
                unidades.Add((precioBase, li));
            }
        }

        if (unidades.Count < 2)
        {
            return null;
        }

        unidades.Sort((a, b) => b.Precio.CompareTo(a.Precio)); // precio desc

        var descuentoTotal = 0.0;
        var descuentoPorLinea = new Dictionary<int, double>();
        for (var i = 0; i + 1 < unidades.Count; i += 2)
        {
            var primero = unidades[i];
            var segundo = unidades[i + 1];
            var precioMasBajo = Math.Min(primero.Precio, segundo.Precio);
            descuentoTotal += precioMasBajo;

            // El descuento se imputa a la línea de la unidad más barata del par.
            if (primero.Precio <= segundo.Precio)
            {
                descuentoPorLinea[primero.LineaIdx] = descuentoPorLinea.GetValueOrDefault(primero.LineaIdx) + precioMasBajo;
            }
            if (segundo.Precio < primero.Precio)
            {
                descuentoPorLinea[segundo.LineaIdx] = descuentoPorLinea.GetValueOrDefault(segundo.LineaIdx) + precioMasBajo;
            }
        }

        var lineasAfectadas = descuentoPorLinea
            .Where(kv => kv.Value > 0)
            .Select(kv => new LineaAfectada(kv.Key, kv.Value))
            .ToList();
        if (lineasAfectadas.Count == 0)
        {
            return null;
        }

        return new OfertaAplicada(oferta.Id, oferta.Nombre, nameof(TipoOferta.DOS_POR_UNO), descuentoTotal, lineasAfectadas);
    }

    private static OfertaAplicada? EvaluarCombo(
        Ofertum oferta, IReadOnlyList<LineaCalculo> lineas, IReadOnlyDictionary<string, double> precios)
    {
        if (oferta.GrupoCombos.Count == 0)
        {
            return null;
        }

        var cantidadPorProducto = new Dictionary<string, int>();
        foreach (var linea in lineas)
        {
            cantidadPorProducto[linea.ProductoId] = cantidadPorProducto.GetValueOrDefault(linea.ProductoId) + linea.Cantidad;
        }

        // Todo grupo obligatorio debe cubrirse con la cantidad pedida de sus opciones.
        foreach (var grupo in oferta.GrupoCombos.Where(g => g.Obligatorio))
        {
            var opciones = grupo.GrupoOpcions.Select(o => o.ProductoId).ToHashSet();
            var encontrada = cantidadPorProducto
                .Where(kv => opciones.Contains(kv.Key))
                .Sum(kv => kv.Value);
            if (encontrada < grupo.Cantidad)
            {
                return null;
            }
        }

        var precioIndividual = 0.0;
        foreach (var grupo in oferta.GrupoCombos)
        {
            foreach (var opcion in grupo.GrupoOpcions)
            {
                var cantidad = cantidadPorProducto.GetValueOrDefault(opcion.ProductoId);
                if (cantidad > 0)
                {
                    var precioProducto = precios.GetValueOrDefault(opcion.ProductoId);
                    precioIndividual += precioProducto * Math.Min(cantidad, grupo.Cantidad);
                }
            }
        }

        double precioCombo;
        if (oferta.MontoDescuento is { } monto)
        {
            precioCombo = monto;
        }
        else
        {
            precioCombo = 0;
            foreach (var producto in oferta.OfertaProductos)
            {
                var cantidad = cantidadPorProducto.GetValueOrDefault(producto.ProductoId);
                if (cantidad > 0)
                {
                    var precio = producto.PrecioEspecial ?? precios.GetValueOrDefault(producto.ProductoId);
                    precioCombo += precio * cantidad;
                }
            }
        }

        var descuento = precioIndividual - precioCombo;
        if (descuento <= 0)
        {
            return null;
        }

        return new OfertaAplicada(oferta.Id, oferta.Nombre, nameof(TipoOferta.COMBO), descuento, Array.Empty<LineaAfectada>());
    }

    private static OfertaAplicada? EvaluarDescuentoPorcentaje(Ofertum oferta, IReadOnlyList<LineaCalculo> lineas)
    {
        var productosOferta = oferta.OfertaProductos.Count > 0
            ? oferta.OfertaProductos.Select(p => p.ProductoId).ToHashSet()
            : null;

        var montoAplicable = 0.0;
        foreach (var linea in lineas)
        {
            if (productosOferta is null || productosOferta.Contains(linea.ProductoId))
            {
                montoAplicable += linea.PrecioUnitario * linea.Cantidad;
            }
        }

        if (montoAplicable == 0)
        {
            return null;
        }

        var porcentaje = (oferta.PorcentajeDescuento ?? 0) / 100;
        var descuento = montoAplicable * porcentaje;
        return new OfertaAplicada(oferta.Id, oferta.Nombre, nameof(TipoOferta.DESCUENTO_PORCENTAJE), descuento, Array.Empty<LineaAfectada>());
    }

    private static OfertaAplicada? EvaluarDescuentoMontoFijo(Ofertum oferta, IReadOnlyList<LineaCalculo> lineas)
    {
        var productosOferta = oferta.OfertaProductos.Count > 0
            ? oferta.OfertaProductos.Select(p => p.ProductoId).ToHashSet()
            : null;

        var aplica = productosOferta is null || lineas.Any(l => productosOferta.Contains(l.ProductoId));
        if (!aplica)
        {
            return null;
        }

        return new OfertaAplicada(
            oferta.Id, oferta.Nombre, nameof(TipoOferta.DESCUENTO_MONTO_FIJO), oferta.MontoDescuento ?? 0, Array.Empty<LineaAfectada>());
    }
}
