using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrbIT.Application.CodigosDescuento;
using OrbIT.Application.Common;
using OrbIT.Application.Demora;
using OrbIT.Application.Ofertas;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Pedidos;

/// <summary>
/// Implementación de <see cref="IPedidoService"/>. Réplica funcional del <c>crearPedido</c> y
/// <c>cancelarPedido</c> de NestJS, con las mejoras aprobadas (upsert de cliente con totales, tenant
/// estructural por Global Query Filter, consumo atómico de ofertas/códigos con <c>ExecuteUpdate</c>).
/// </summary>
public sealed class PedidoService : IPedidoService
{
    private const string TipoDescuentoPedido = "DESCUENTO_PEDIDO";
    private const string TipoDevolucionCancelacion = "DEVOLUCION_CANCELACION";
    private const int DefaultCostoEnvio = 3000;
    private const int DefaultMaxAderezosGratis = 2;

    private static readonly EstadoPedido[] EstadosAbiertos =
    {
        EstadoPedido.PENDIENTE, EstadoPedido.EN_PREPARACION, EstadoPedido.EN_HORNO,
        EstadoPedido.LISTO_PARA_RETIRAR, EstadoPedido.EN_CAMINO,
    };

    private readonly OrbitDbContext _db;
    private readonly IOfertasCalculatorService _ofertas;
    private readonly ICodigosDescuentoService _codigos;
    private readonly IDemoraService _demora;
    private readonly IPedidoNotificationService _notifier;

    public PedidoService(
        OrbitDbContext db,
        IOfertasCalculatorService ofertas,
        ICodigosDescuentoService codigos,
        IDemoraService demora,
        IPedidoNotificationService notifier)
    {
        _db = db;
        _ofertas = ofertas;
        _codigos = codigos;
        _demora = demora;
        _notifier = notifier;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CREAR
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<string> CrearPedidoAsync(CrearPedidoInput input, string negocioId, CancellationToken ct = default)
    {
        await ValidarHorarioAsync(negocioId, ct);

        var detalles = input.Detalles;
        if (detalles.Count == 0)
        {
            throw PedidoException.BadRequest("El pedido no tiene productos");
        }
        if (input.Tipo == TipoPedido.DELIVERY && string.IsNullOrWhiteSpace(input.Direccion))
        {
            throw PedidoException.BadRequest("La dirección es obligatoria para DELIVERY");
        }
        if (input.CuentaAbierta && input.Tipo == TipoPedido.DELIVERY)
        {
            throw PedidoException.BadRequest("Los pedidos de delivery no pueden tener cuenta abierta");
        }
        if (input.PedidoId is null && input.Tipo == TipoPedido.LOCAL)
        {
            var modoMesas = await ConfigAsync("modo_mesas", ct);
            if (modoMesas == "true" && string.IsNullOrEmpty(input.MesaId))
            {
                throw PedidoException.BadRequest("La mesa es obligatoria para pedidos LOCAL cuando el modo mesas está activado");
            }
        }

        var costoEnvioFinal = await ResolverCostoEnvioAsync(input, ct);

        var nombreCliente = TrimOrNull(input.NombreCliente);
        var apellidoCliente = TrimOrNull(input.ApellidoCliente);
        var numeroCliente = TrimOrNull(input.NumeroCliente);
        if (input.PedidoId is null)
        {
            if (nombreCliente is null) throw PedidoException.BadRequest("nombreCliente es obligatorio");
            if (apellidoCliente is null) throw PedidoException.BadRequest("apellidoCliente es obligatorio");
            if (numeroCliente is null || numeroCliente.Length < 8) throw PedidoException.BadRequest("Teléfono debe tener al menos 8 dígitos");
        }

        int? demoraEstimadaMin = input.PedidoId is null
            ? await _demora.CalcularDemoraEstimadaAsync(detalles.Select(d => d.ProductoId).ToList(), negocioId, ct)
            : null;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await ValidarMesaYRepartidorAsync(input, negocioId, ct);

        // ── Cargar productos/extras/aderezos (tenant-scoped por el Global Query Filter) ──
        var sabor2Ids = detalles.Where(d => d.MediaMedia?.Sabor2Id is not null).Select(d => d.MediaMedia!.Sabor2Id!);
        var todosProductoIds = detalles.Select(d => d.ProductoId).Concat(sabor2Ids).Distinct().ToList();
        var prodMap = await CargarProductosAsync(todosProductoIds, ct);
        foreach (var d in detalles)
        {
            if (!prodMap.TryGetValue(d.ProductoId, out var prod))
                throw PedidoException.BadRequest($"Producto no encontrado o no pertenece a este negocio: {d.ProductoId}");
            if (!prod.Activo) throw PedidoException.BadRequest($"Producto inactivo: {d.ProductoId}");
        }

        var extraIds = detalles.SelectMany(d => d.Extras ?? Array.Empty<PedidoExtraInput>()).Select(e => e.ExtraId).Distinct().ToList();
        var extraMap = await CargarExtrasAsync(extraIds, ct);

        var aderezoIds = detalles.SelectMany(d => d.AderezosIds ?? Array.Empty<string>()).Distinct().ToList();
        var aderezoMap = await CargarAderezosAsync(aderezoIds, ct);
        foreach (var adeId in aderezoIds)
        {
            if (!aderezoMap.TryGetValue(adeId, out var ade))
                throw PedidoException.BadRequest($"Aderezo no encontrado o no pertenece a este negocio: {adeId}");
            if (!ade.Activo) throw PedidoException.BadRequest($"Aderezo inactivo: {ade.Nombre}");
            if (ade.StockActual <= 0) throw PedidoException.BadRequest($"Sin stock de aderezo: {ade.Nombre}");
        }

        // ── Validación de stock (recetas + extras + aderezos) ──
        var stockChecks = new List<(string InsumoId, double Requerido)>();
        foreach (var d in detalles)
        {
            var cantidad = d.Cantidad;
            var prod = prodMap[d.ProductoId];
            var factorMedia = d.MediaMedia?.Sabor2Id is not null ? 0.5 : 1.0;

            foreach (var r in prod.Receta)
                stockChecks.Add((r.InsumoId, r.Cantidad * cantidad * factorMedia));

            if (d.MediaMedia?.Sabor2Id is { } s2 && prodMap.TryGetValue(s2, out var prod2))
            {
                foreach (var r in prod2.Receta)
                    stockChecks.Add((r.InsumoId, r.Cantidad * cantidad * 0.5));
            }

            var categoriaId = prod.CategoriaId;

            foreach (var e in d.Extras ?? Array.Empty<PedidoExtraInput>())
            {
                if (!extraMap.TryGetValue(e.ExtraId, out var extra))
                    throw PedidoException.BadRequest($"Extra no encontrado o no pertenece a este negocio: {e.ExtraId}");
                if (!extra.Activo) throw PedidoException.BadRequest($"Extra inactivo: {e.ExtraId}");

                var cantidadExtra = e.Cantidad;
                var stockDisponible = extra.InsumoId is { } insId
                    ? await StockInsumoAsync(insId, ct)
                    : extra.StockActual;
                var consumo = GetExtraConsumo(extra, categoriaId);
                var requerido = consumo * cantidadExtra;
                if (stockDisponible < requerido)
                    throw PedidoException.BadRequest($"Stock insuficiente para extra {extra.Nombre}. Disponible: {stockDisponible}, Requerido: {requerido} (consumo={consumo})");
            }

            foreach (var adeId in d.AderezosIds ?? Array.Empty<string>())
            {
                if (aderezoMap.TryGetValue(adeId, out var ade))
                {
                    var consumo = GetAderezoConsumo(ade, categoriaId);
                    var requerido = consumo * cantidad;
                    if (ade.StockActual < requerido)
                        throw PedidoException.BadRequest($"Stock insuficiente de aderezo {ade.Nombre}. Disponible: {ade.StockActual}, Requerido: {requerido} (consumo={consumo})");
                }
            }
        }

        // Agregado de receta por insumo.
        var stockRequeridoPorInsumo = new Dictionary<string, double>();
        foreach (var (insumoId, req) in stockChecks)
            stockRequeridoPorInsumo[insumoId] = stockRequeridoPorInsumo.GetValueOrDefault(insumoId) + req;
        foreach (var (insumoId, requerido) in stockRequeridoPorInsumo)
        {
            var disponible = await StockInsumoAsync(insumoId, ct);
            if (disponible < requerido)
            {
                var nombre = await _db.Insumos.AsNoTracking().Where(i => i.Id == insumoId).Select(i => i.Nombre).FirstOrDefaultAsync(ct) ?? insumoId;
                throw PedidoException.BadRequest($"Stock insuficiente de {nombre}. Disponible: {disponible}, Requerido: {requerido}");
            }
        }

        // ── Armado de detalles + pricing (extras gratis/cobrado, aderezos gratis) ──
        var totalNuevosItems = 0.0;
        var subtotalPorProducto = new Dictionary<string, double>();
        var detallesEntidades = new List<PedidoDetalle>();
        var extrasADescontar = new List<(string ExtraId, int Cantidad, string? CategoriaId)>();
        var aderezosADescontar = new List<(string AderezoId, int Cantidad, string? CategoriaId)>();

        foreach (var d in detalles)
        {
            var prod = prodMap[d.ProductoId];
            var cantidad = d.Cantidad;
            var precioUnitario = prod.Precio;
            var categoriaId = prod.CategoriaId;
            var extrasNorm = (d.Extras ?? Array.Empty<PedidoExtraInput>())
                .Select(e => (ExtraId: e.ExtraId, Cantidad: e.Cantidad <= 0 ? 1 : e.Cantidad)).ToList();
            foreach (var e in extrasNorm) extrasADescontar.Add((e.ExtraId, e.Cantidad, categoriaId));

            // Expandir extras en unidades.
            var expanded = new List<ExtraData>();
            foreach (var e in extrasNorm)
            {
                if (extraMap.TryGetValue(e.ExtraId, out var extra))
                    for (var i = 0; i < e.Cantidad; i++) expanded.Add(extra);
            }

            // Conteo por toppingGrupo para los "gratis".
            var grupoCounts = new Dictionary<string, (int Incluido, int Max)>();
            foreach (var extra in expanded)
            {
                if (extra.ToppingGrupoId is { } gid && extra.ToppingGrupo is { } tg)
                {
                    var gc = grupoCounts.TryGetValue(gid, out var existing) ? existing : (Incluido: 0, Max: tg.MaxExtrasGratis);
                    if (!extra.EsPremium && tg.EsIncluido) gc.Incluido++;
                    grupoCounts[gid] = gc;
                }
            }
            var grupoFreeRemaining = grupoCounts.ToDictionary(kv => kv.Key, kv => Math.Min(kv.Value.Incluido, kv.Value.Max));

            var extrasCobradoTotal = 0.0;
            var extrasSnapshot = new List<ExtraSnapshot>();
            foreach (var extra in expanded)
            {
                var precioExtra = GetExtraPrecio(extra, categoriaId);
                var esPremium = extra.EsPremium;
                bool cobrado;
                if (esPremium)
                {
                    cobrado = true;
                }
                else if (extra.ToppingGrupoId is { } gid && extra.ToppingGrupo?.EsIncluido == true)
                {
                    var remaining = grupoFreeRemaining.GetValueOrDefault(gid);
                    if (remaining > 0) { grupoFreeRemaining[gid] = remaining - 1; cobrado = false; }
                    else cobrado = true;
                }
                else
                {
                    cobrado = true;
                }

                var precioFinal = cobrado ? precioExtra : 0;
                extrasCobradoTotal += precioFinal;
                extrasSnapshot.Add(new ExtraSnapshot(extra.Id, extra.Nombre, precioExtra, cobrado));
            }

            var precioFinalUnitario = precioUnitario;
            if (d.MediaMedia?.Sabor2Id is { } sab2 && prodMap.TryGetValue(sab2, out var prodSab2))
                precioFinalUnitario = Math.Max(precioFinalUnitario, prodSab2.Precio);

            // Aderezos gratis hasta maxAderezosGratis de la categoría.
            var aderezosCobrado = 0.0;
            var aderezosLinea = d.AderezosIds ?? Array.Empty<string>();
            if (aderezosLinea.Count > 0)
            {
                var maxGratis = categoriaId is null
                    ? DefaultMaxAderezosGratis
                    : await _db.Categoria.AsNoTracking().Where(c => c.Id == categoriaId).Select(c => (int?)c.MaxAderezosGratis).FirstOrDefaultAsync(ct) ?? DefaultMaxAderezosGratis;
                var gratisCount = 0;
                foreach (var adeId in aderezosLinea)
                {
                    if (!aderezoMap.TryGetValue(adeId, out var ade)) continue;
                    if (ade.EsPremium) aderezosCobrado += ade.Precio;
                    else if (gratisCount >= maxGratis) aderezosCobrado += ade.Precio;
                    else gratisCount++;
                }
                foreach (var adeId in aderezosLinea) aderezosADescontar.Add((adeId, cantidad, categoriaId));
            }

            var subtotal = precioFinalUnitario * cantidad + extrasCobradoTotal + aderezosCobrado;
            totalNuevosItems += subtotal;
            subtotalPorProducto[d.ProductoId] = subtotalPorProducto.GetValueOrDefault(d.ProductoId) + subtotal;

            var detalle = new PedidoDetalle
            {
                Id = Guid.NewGuid().ToString(),
                NegocioId = negocioId,
                ProductoId = d.ProductoId,
                Cantidad = cantidad,
                PrecioUnitario = precioFinalUnitario,
                Subtotal = subtotal,
                Notas = TrimOrNull(d.Notas),
                SinExtras = d.SinExtras,
                ImpresoEnCocina = d.ImpresoEnCocina,
                Extras = extrasSnapshot.Count > 0 ? JsonSerializer.Serialize(extrasSnapshot) : null,
            };
            foreach (var adeId in aderezosLinea)
                if (aderezoMap.TryGetValue(adeId, out var ade)) detalle.As.Add(ade.Entidad);
            if (d.MediaMedia is { Sabor1Id: { } s1, Sabor2Id: { } s2b })
                detalle.PizzaMediaMedium = new PizzaMediaMedium { Id = Guid.NewGuid().ToString(), Sabor1Id = s1, Sabor2Id = s2b, NegocioId = negocioId };

            detallesEntidades.Add(detalle);
        }

        // ── Ofertas: calcular + consumir uso de forma atómica (race-safe) ──
        var lineasCalc = detalles
            .Select(d => new LineaCalculo(d.ProductoId, d.Cantidad, prodMap[d.ProductoId].Precio, d.MediaMedia?.Sabor2Id is not null))
            .ToList();
        var calculo = await _ofertas.CalcularAsync(lineasCalc, negocioId, ct);

        var ofertasConfirmadas = new List<OfertaAplicada>();
        if (calculo.OfertasAplicadas.Count > 0)
        {
            var ids = calculo.OfertasAplicadas.Select(o => o.OfertaId).ToList();
            var maxUsos = await _db.Oferta.AsNoTracking().Where(o => ids.Contains(o.Id))
                .Select(o => new { o.Id, o.MaxUsosTotales }).ToDictionaryAsync(o => o.Id, o => o.MaxUsosTotales, ct);

            foreach (var aplicada in calculo.OfertasAplicadas)
            {
                if (maxUsos.TryGetValue(aplicada.OfertaId, out var max) && max is { } limite)
                {
                    var usada = await _db.Oferta
                        .Where(o => o.Id == aplicada.OfertaId && o.UsosActuales < limite)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.UsosActuales, o => o.UsosActuales + 1), ct);
                    if (usada == 1) ofertasConfirmadas.Add(aplicada);
                }
                else
                {
                    await _db.Oferta.Where(o => o.Id == aplicada.OfertaId)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.UsosActuales, o => o.UsosActuales + 1), ct);
                    ofertasConfirmadas.Add(aplicada);
                }
            }
        }
        var descuentoOfertas = ofertasConfirmadas.Sum(o => o.Descuento);

        // ── Código de descuento: validar + tope 50% + consumir uso atómico ──
        var (descuentoCodigo, codigoId) = await ResolverCodigoAsync(input.CodigoDescuento, negocioId, totalNuevosItems, descuentoOfertas, subtotalPorProducto, ct);

        var totalConOfertas = Math.Max(0, totalNuevosItems - descuentoOfertas - descuentoCodigo);

        // ── Persistir pedido (create o add-items a cuenta abierta) ──
        string pedidoIdResult;
        if (input.PedidoId is { } existingId)
        {
            var pedido = await _db.Pedidos.FirstOrDefaultAsync(p => p.Id == existingId, ct)
                ?? throw PedidoException.NotFound("Pedido no encontrado");
            if (!PuedeAgregarItems(pedido)) throw PedidoException.BadRequest("No se pueden agregar items a este pedido");

            pedido.Total += totalConOfertas;
            if (pedido.NombreCliente is null && nombreCliente is not null) pedido.NombreCliente = nombreCliente;
            if (pedido.ApellidoCliente is null && apellidoCliente is not null) pedido.ApellidoCliente = apellidoCliente;
            if (pedido.NumeroCliente is null && numeroCliente is not null) pedido.NumeroCliente = numeroCliente;
            if (pedido.MetodoPago is null && input.MetodoPago is { } mp) pedido.MetodoPago = mp;
            // costoEnvio se ignora a propósito en pedidos existentes (se mantiene el valor de DB).
            if (input.RepartidorId is not null) pedido.RepartidorId = input.RepartidorId;
            if (input.MesaId is not null) pedido.MesaId = input.MesaId;
            foreach (var det in detallesEntidades) { det.PedidoId = pedido.Id; _db.PedidoDetalles.Add(det); }
            await _db.SaveChangesAsync(ct);
            pedidoIdResult = pedido.Id;
        }
        else
        {
            var pedido = new Pedido
            {
                Id = Guid.NewGuid().ToString(),
                NegocioId = negocioId,
                Tipo = input.Tipo,
                NombreCliente = nombreCliente,
                ApellidoCliente = apellidoCliente,
                NumeroCliente = numeroCliente,
                MetodoPago = input.MetodoPago,
                Direccion = input.Tipo == TipoPedido.DELIVERY ? input.Direccion!.Trim() : null,
                CostoEnvio = costoEnvioFinal,
                DireccionLat = input.DireccionLat,
                DireccionLng = input.DireccionLng,
                DireccionFormateada = input.DireccionFormateada,
                Piso = input.Piso,
                Departamento = input.Departamento,
                Referencias = input.Referencias,
                NotasRepartidor = input.NotasRepartidor,
                ShippingZoneName = input.ShippingZoneName,
                ShippingReason = input.ShippingReason,
                DireccionPrecision = input.DireccionPrecision,
                RepartidorId = input.RepartidorId,
                MesaId = input.MesaId,
                Total = totalConOfertas,
                Estado = EstadoPedido.PENDIENTE,
                EstadoPago = input.EsAutenticado ? (input.EstadoPago ?? EstadoPago.PENDIENTE) : EstadoPago.PENDIENTE,
                CuentaAbierta = input.CuentaAbierta,
                DemoraEstimadaMin = demoraEstimadaMin,
                CreatedAt = Now(),
            };
            foreach (var det in detallesEntidades) pedido.PedidoDetalles.Add(det);
            _db.Pedidos.Add(pedido);
            await _db.SaveChangesAsync(ct);
            pedidoIdResult = pedido.Id;
        }

        // ── Descontar stock (extras / aderezos / recetas) + StockMovimiento ──
        await DescontarStockExtrasAsync(extrasADescontar, extraMap, pedidoIdResult, negocioId, ct);
        await DescontarStockAderezosAsync(aderezosADescontar, aderezoMap, pedidoIdResult, negocioId, ct);
        await DescontarStockRecetasAsync(stockRequeridoPorInsumo, pedidoIdResult, negocioId, ct);

        // ── PedidoOferta (snapshots) ──
        foreach (var aplicada in ofertasConfirmadas)
        {
            _db.PedidoOferta.Add(new PedidoOfertum
            {
                Id = Guid.NewGuid().ToString(),
                NegocioId = negocioId,
                PedidoId = pedidoIdResult,
                OfertaId = aplicada.OfertaId,
                PrecioOriginal = calculo.Subtotal,
                PrecioFinal = Math.Max(0, calculo.Subtotal - descuentoOfertas),
                DescuentoAplicado = aplicada.Descuento,
            });
        }
        if (codigoId is not null) await _codigos.IncrementarUsoAsync(codigoId, ct);

        // ── Cliente: upsert con totales (mejora aprobada) — solo en pedido nuevo ──
        if (input.PedidoId is null && numeroCliente is not null)
        {
            await UpsertClienteAsync(numeroCliente, nombreCliente, apellidoCliente, totalConOfertas, pedidoIdResult, negocioId, ct);
        }

        // ── Ocupar mesa atómicamente (solo si sigue LIBRE) ──
        if (input.MesaId is { } mesaId)
        {
            var ocupada = await _db.Mesas
                .Where(m => m.Id == mesaId && m.Estado == EstadoMesa.LIBRE)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Estado, EstadoMesa.OCUPADA)
                    .SetProperty(m => m.PedidoActivoId, pedidoIdResult), ct);
            if (ocupada != 1) throw PedidoException.BadRequest("La mesa ya fue ocupada por otro pedido");
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // SignalR: solo pedidos entrados por el menú público notifican al panel (paridad con NestJS, donde el
        // gateway emitía 'nuevo-pedido' a la room = negocioId). Best-effort tras el commit: el notifier traga
        // errores, nunca rompe la creación. El timestamp replica el new Date().toISOString() del original
        // (UTC, milisegundos, sufijo 'Z').
        if (string.Equals(input.Origen, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            await _notifier.NotificarNuevoPedidoAsync(negocioId, new NuevoPedidoNotification(
                pedidoIdResult,
                nombreCliente ?? string.Empty,
                apellidoCliente ?? string.Empty,
                numeroCliente ?? string.Empty,
                input.Tipo.ToString(),
                totalConOfertas,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)), ct);
        }

        return pedidoIdResult;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CANCELAR (reversión de stock)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task CancelarPedidoAsync(string pedidoId, string motivo, Role canceladoPor, string negocioId, CancellationToken ct = default)
    {
        var motivoLimpio = (motivo ?? string.Empty).Trim();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var pedido = await _db.Pedidos
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.Producto).ThenInclude(pr => pr.ProductoReceta)
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.As)
            .Include(p => p.PedidoDetalles).ThenInclude(d => d.PizzaMediaMedium)
            .FirstOrDefaultAsync(p => p.Id == pedidoId, ct)
            ?? throw PedidoException.NotFound("Pedido no encontrado");

        if (pedido.Estado == EstadoPedido.CANCELADO) throw PedidoException.BadRequest("El pedido ya estaba cancelado");
        if (pedido.Estado == EstadoPedido.ENTREGADO) throw PedidoException.BadRequest("No se puede cancelar un pedido entregado");
        if (motivoLimpio.Length == 0) throw PedidoException.BadRequest("Motivo obligatorio");

        foreach (var detalle in pedido.PedidoDetalles)
        {
            var categoriaId = detalle.Producto.CategoriaId;

            // Extras (snapshot jsonb): reponer por la cantidad real consumida.
            if (detalle.Extras is { } extrasJson)
            {
                var snapshot = JsonSerializer.Deserialize<List<ExtraSnapshot>>(extrasJson) ?? new();
                var counts = snapshot.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Count());
                foreach (var (extraId, count) in counts)
                {
                    var extra = await CargarExtraParaReponerAsync(extraId, ct);
                    if (extra is null) continue;
                    var reponer = GetExtraConsumo(extra, categoriaId) * count;
                    if (extra.InsumoId is { } insId)
                        await ReponerStockAsync(_db.Insumos, insId, reponer, negocioId, pedidoId, insumoId: insId, extraId: extraId, aderezoId: null, $"Devuelto por cancelación (topping {extra.Nombre})", ct);
                    else
                        await ReponerStockExtraAsync(extraId, reponer, negocioId, pedidoId, $"Devuelto por cancelación (topping {extra.Nombre})", ct);
                }
            }

            // Aderezos.
            foreach (var ade in detalle.As)
            {
                var consumoData = await CargarAderezoParaReponerAsync(ade.Id, ct);
                if (consumoData is null) continue;
                var reponer = GetAderezoConsumo(consumoData, categoriaId) * detalle.Cantidad;
                await ReponerStockAderezoAsync(ade.Id, reponer, negocioId, pedidoId, "Devuelto por cancelación (salsa)", ct);
            }

            // Recetas (factor 0.5 en media-media + receta del sabor2).
            var factor = detalle.PizzaMediaMedium?.Sabor2Id is not null ? 0.5 : 1.0;
            var reponerReceta = new List<(string InsumoId, double Cantidad)>();
            foreach (var r in detalle.Producto.ProductoReceta)
                reponerReceta.Add((r.InsumoId, r.Cantidad * detalle.Cantidad * factor));
            if (detalle.PizzaMediaMedium?.Sabor2Id is { } s2)
            {
                var receta2 = await _db.ProductoReceta.AsNoTracking().Where(r => r.ProductoId == s2)
                    .Select(r => new { r.InsumoId, r.Cantidad }).ToListAsync(ct);
                foreach (var r in receta2) reponerReceta.Add((r.InsumoId, r.Cantidad * detalle.Cantidad * 0.5));
            }
            foreach (var (insumoId, cantidad) in reponerReceta)
                await ReponerStockAsync(_db.Insumos, insumoId, cantidad, negocioId, pedidoId, insumoId: insumoId, extraId: null, aderezoId: null, "Devuelto por cancelación de pedido", ct);
        }

        pedido.Estado = EstadoPedido.CANCELADO;
        pedido.MotivoCancelacion = motivoLimpio;
        pedido.CanceladoPor = canceladoPor;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers de carga / config
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<string?> ConfigAsync(string clave, CancellationToken ct) =>
        await _db.Configuracions.AsNoTracking().Where(c => c.Clave == clave).Select(c => c.Valor).FirstOrDefaultAsync(ct);

    private async Task ValidarHorarioAsync(string negocioId, CancellationToken ct)
    {
        var horaApertura = await ConfigAsync("hora_apertura", ct);
        var horaCierre = await ConfigAsync("hora_cierre", ct);
        if (string.IsNullOrEmpty(horaApertura) || string.IsNullOrEmpty(horaCierre)) return;

        var diasStr = await ConfigAsync("dias_atencion", ct);
        var dias = string.IsNullOrEmpty(diasStr)
            ? new List<int> { 1, 2, 3, 4, 5, 6, 7 }
            : diasStr.Split(',').Select(s => int.TryParse(s.Trim(), out var n) ? n : -1).Where(n => n is >= 1 and <= 7).ToList();

        var estado = HorarioComercial.CalcularEstado(horaApertura, horaCierre, dias);
        if (estado.Abierto) return;

        throw estado.Razon switch
        {
            RazonEstadoLocal.DiaNoLaboral => PedidoException.BadRequest(estado.ProximaApertura is { } pr
                ? $"Hoy no atendemos. Próxima apertura: {HorarioComercial.NombresDias.GetValueOrDefault(pr.Dia, "")} a las {pr.Hora}"
                : "Hoy no atendemos."),
            RazonEstadoLocal.ConfigInvalida => PedidoException.BadRequest("El horario del local no está configurado correctamente. Contactá al administrador."),
            _ => PedidoException.BadRequest($"Estamos cerrados. Horario de atención: {horaApertura} a {horaCierre}"),
        };
    }

    private async Task<double> ResolverCostoEnvioAsync(CrearPedidoInput input, CancellationToken ct)
    {
        if (input.Tipo != TipoPedido.DELIVERY) return 0;
        if (input.PedidoId is not null) return input.CostoEnvio ?? 0;

        var minimoStr = await ConfigAsync("delivery_precio_base", ct);
        var minimo = int.TryParse(minimoStr, out var m) ? m : DefaultCostoEnvio;
        return input.CostoEnvio is { } c && c >= minimo ? c : minimo;
    }

    private async Task ValidarMesaYRepartidorAsync(CrearPedidoInput input, string negocioId, CancellationToken ct)
    {
        if (input.MesaId is { } mesaId)
        {
            // El query filter ya scopea al tenant → mesa ajena = null = "no encontrada".
            var mesa = await _db.Mesas.AsNoTracking().Where(m => m.Id == mesaId)
                .Select(m => new { m.Estado, m.Activa }).FirstOrDefaultAsync(ct)
                ?? throw PedidoException.BadRequest("Mesa no encontrada");
            if (mesa.Estado != EstadoMesa.LIBRE) throw PedidoException.BadRequest("La mesa no está disponible (OCUPADA)");
            if (!mesa.Activa) throw PedidoException.BadRequest("La mesa no está activa");
        }

        if (input.RepartidorId is { } repId)
        {
            // Users NO tiene Global Query Filter por tenant → comparar negocioId a mano.
            var rep = await _db.Users.Where(u => u.Id == repId)
                .Select(u => new { u.NegocioId, u.Role, u.Activo }).FirstOrDefaultAsync(ct);
            if (rep is null || rep.NegocioId != negocioId) throw PedidoException.BadRequest("Repartidor no encontrado");
            if (rep.Role != Role.DELIVERY) throw PedidoException.BadRequest("El usuario indicado no es repartidor");
            if (!rep.Activo) throw PedidoException.BadRequest("El repartidor no está activo");
        }
    }

    private async Task<Dictionary<string, ProductoData>> CargarProductosAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        (await _db.Productos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new ProductoData(
                p.Id, p.Precio, p.Activo, p.Nombre, p.CategoriaId,
                p.ProductoReceta.Select(r => new RecetaItem(r.InsumoId, r.Cantidad)).ToList()))
            .ToListAsync(ct))
        .ToDictionary(p => p.Id);

    private async Task<Dictionary<string, ExtraData>> CargarExtrasAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        return (await _db.Extras.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new ExtraData(
                e.Id, e.Nombre, e.Precio, e.StockActual, e.Activo, e.EsPremium, e.InsumoId, e.ToppingGrupoId,
                e.ToppingGrupo == null ? null : new ToppingGrupoData(e.ToppingGrupo.MaxExtrasGratis, e.ToppingGrupo.EsIncluido),
                e.ExtraPrecios.Select(pp => new CategoriaValor(pp.CategoriaId, pp.Precio)).ToList(),
                e.ExtraConsumos.Select(cc => new CategoriaValor(cc.CategoriaId, cc.CantidadConsumo)).ToList()))
            .ToListAsync(ct))
        .ToDictionary(e => e.Id);
    }

    private async Task<Dictionary<string, AderezoData>> CargarAderezosAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var entidades = await _db.Aderezos.Include(a => a.AderezoConsumos).Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        return entidades.ToDictionary(
            a => a.Id,
            a => new AderezoData(a.Id, a.Nombre, a.StockActual, a.Activo, a.EsPremium, a.Precio,
                a.AderezoConsumos.Select(c => new CategoriaValor(c.CategoriaId, c.CantidadConsumo)).ToList(), a));
    }

    private Task<double> StockInsumoAsync(string insumoId, CancellationToken ct) =>
        _db.Insumos.AsNoTracking().Where(i => i.Id == insumoId).Select(i => i.StockActual).FirstOrDefaultAsync(ct);

    // ── Resolución de código de descuento (validación + tope 50% + consumo atómico) ──
    private async Task<(double Descuento, string? CodigoId)> ResolverCodigoAsync(
        string? codigoDescuento, string negocioId, double totalNuevosItems, double descuentoOfertas,
        IReadOnlyDictionary<string, double> subtotalPorProducto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(codigoDescuento)) return (0, null);

        var resultado = await _codigos.ValidarAsync(codigoDescuento, negocioId, productoId: null, ct);
        if (!resultado.Valido || resultado.Codigo is not { } codigo) return (0, null);

        double descuento;
        if (codigo.ProductoId is { } restringidoA)
        {
            var subtotalProducto = subtotalPorProducto.GetValueOrDefault(restringidoA);
            if (subtotalProducto <= 0) return (0, null); // el producto no está en el pedido → se ignora
            descuento = codigo.TipoDescuento == "PORCENTAJE"
                ? RoundHalfUp(subtotalProducto * codigo.Valor / 100)
                : Math.Min(codigo.Valor, subtotalProducto);
        }
        else
        {
            var subtotalParaCodigo = totalNuevosItems - descuentoOfertas;
            descuento = codigo.TipoDescuento == "PORCENTAJE"
                ? RoundHalfUp(subtotalParaCodigo * codigo.Valor / 100)
                : Math.Min(codigo.Valor, subtotalParaCodigo);
        }

        // Tope combinado (oferta + código) ≤ 50% del subtotal relevante. Solo se recorta el código.
        if (descuento > 0)
        {
            var subtotalRelevante = codigo.ProductoId is { } p ? subtotalPorProducto.GetValueOrDefault(p) : totalNuevosItems;
            var tope = subtotalRelevante * 0.5;
            if (descuentoOfertas + descuento > tope)
            {
                var permitido = Math.Max(0, tope - descuentoOfertas);
                if (permitido < descuento) descuento = permitido;
            }
        }
        if (descuento <= 0) return (0, null);

        // Consumo atómico race-safe (solo si aplica descuento real).
        if (codigo.UsosMaximos is { } max)
        {
            var usado = await _db.CodigoDescuentos
                .Where(c => c.Id == codigo.Id && c.UsosActuales < max)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsosActuales, c => c.UsosActuales + 1), ct);
            if (usado != 1) return (0, null); // perdió la carrera → sin descuento
            return (descuento, null); // ya incrementado acá; no volver a incrementar en el caller
        }

        return (descuento, codigo.Id); // sin tope de usos → el caller incrementa vía IncrementarUsoAsync
    }

    // ── Descuento de stock (create) ──
    private async Task DescontarStockExtrasAsync(
        List<(string ExtraId, int Cantidad, string? CategoriaId)> extras,
        IReadOnlyDictionary<string, ExtraData> extraMap, string pedidoId, string negocioId, CancellationToken ct)
    {
        foreach (var (extraId, cantidad, categoriaId) in extras)
        {
            var extra = extraMap[extraId];
            var totalDescontar = GetExtraConsumo(extra, categoriaId) * cantidad;
            if (extra.InsumoId is { } insId)
            {
                var antes = await StockInsumoAsync(insId, ct);
                var n = await _db.Insumos.Where(i => i.Id == insId && i.StockActual >= totalDescontar)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual - totalDescontar), ct);
                if (n == 0) throw PedidoException.BadRequest($"Stock insuficiente para extra {extra.Nombre}. Disponible: {antes}, Solicitado: {totalDescontar}");
                AddMovimiento(negocioId, pedidoId, totalDescontar, antes, TipoDescuentoPedido, insumoId: insId, extraId: extraId, aderezoId: null, $"Consumo por extra: {extra.Nombre}");
            }
            else
            {
                var antes = extra.StockActual;
                var n = await _db.Extras.Where(e => e.Id == extraId && e.StockActual >= totalDescontar)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.StockActual, e => e.StockActual - totalDescontar), ct);
                if (n == 0) throw PedidoException.BadRequest($"Stock insuficiente para extra {extra.Nombre}. Disponible: {antes}, Solicitado: {totalDescontar}");
                AddMovimiento(negocioId, pedidoId, totalDescontar, antes, TipoDescuentoPedido, insumoId: null, extraId: extraId, aderezoId: null, "Consumo por pedido");
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task DescontarStockAderezosAsync(
        List<(string AderezoId, int Cantidad, string? CategoriaId)> aderezos,
        IReadOnlyDictionary<string, AderezoData> aderezoMap, string pedidoId, string negocioId, CancellationToken ct)
    {
        foreach (var (aderezoId, cantidad, categoriaId) in aderezos)
        {
            var ade = aderezoMap[aderezoId];
            var totalDescontar = GetAderezoConsumo(ade, categoriaId) * cantidad;
            var antes = await _db.Aderezos.AsNoTracking().Where(a => a.Id == aderezoId).Select(a => a.StockActual).FirstOrDefaultAsync(ct);
            var n = await _db.Aderezos.Where(a => a.Id == aderezoId && a.StockActual >= totalDescontar)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StockActual, a => a.StockActual - totalDescontar), ct);
            if (n == 0) throw PedidoException.BadRequest($"Stock insuficiente de aderezo {ade.Nombre}. Disponible: {antes}, Necesario: {totalDescontar}");
            AddMovimiento(negocioId, pedidoId, totalDescontar, antes, TipoDescuentoPedido, insumoId: null, extraId: null, aderezoId: aderezoId, "Consumo por pedido");
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task DescontarStockRecetasAsync(
        IReadOnlyDictionary<string, double> stockRequeridoPorInsumo, string pedidoId, string negocioId, CancellationToken ct)
    {
        foreach (var (insumoId, requerido) in stockRequeridoPorInsumo)
        {
            var antes = await StockInsumoAsync(insumoId, ct);
            var n = await _db.Insumos.Where(i => i.Id == insumoId && i.StockActual >= requerido)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual - requerido), ct);
            if (n == 0)
            {
                var nombre = await _db.Insumos.AsNoTracking().Where(i => i.Id == insumoId).Select(i => i.Nombre).FirstOrDefaultAsync(ct) ?? insumoId;
                throw PedidoException.BadRequest($"Stock insuficiente de {nombre}. Disponible: {antes}, Requerido: {requerido}");
            }
            AddMovimiento(negocioId, pedidoId, requerido, antes, TipoDescuentoPedido, insumoId: insumoId, extraId: null, aderezoId: null, "Consumo por pedido");
        }
        await _db.SaveChangesAsync(ct);
    }

    // ── Reposición de stock (cancelar) ──
    private async Task ReponerStockAsync(
        Microsoft.EntityFrameworkCore.DbSet<Insumo> set, string id, double cantidad, string negocioId, string pedidoId,
        string? insumoId, string? extraId, string? aderezoId, string motivo, CancellationToken ct)
    {
        var antes = await StockInsumoAsync(id, ct);
        await set.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.StockActual, i => i.StockActual + cantidad), ct);
        AddMovimientoReposicion(negocioId, pedidoId, cantidad, antes, insumoId, extraId, aderezoId, motivo);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ReponerStockExtraAsync(string extraId, double cantidad, string negocioId, string pedidoId, string motivo, CancellationToken ct)
    {
        var antes = await _db.Extras.AsNoTracking().Where(e => e.Id == extraId).Select(e => e.StockActual).FirstOrDefaultAsync(ct);
        await _db.Extras.Where(e => e.Id == extraId).ExecuteUpdateAsync(s => s.SetProperty(e => e.StockActual, e => e.StockActual + cantidad), ct);
        AddMovimientoReposicion(negocioId, pedidoId, cantidad, antes, insumoId: null, extraId: extraId, aderezoId: null, motivo);
        await _db.SaveChangesAsync(ct);
    }

    private async Task ReponerStockAderezoAsync(string aderezoId, double cantidad, string negocioId, string pedidoId, string motivo, CancellationToken ct)
    {
        var antes = await _db.Aderezos.AsNoTracking().Where(a => a.Id == aderezoId).Select(a => a.StockActual).FirstOrDefaultAsync(ct);
        await _db.Aderezos.Where(a => a.Id == aderezoId).ExecuteUpdateAsync(s => s.SetProperty(a => a.StockActual, a => a.StockActual + cantidad), ct);
        AddMovimientoReposicion(negocioId, pedidoId, cantidad, antes, insumoId: null, extraId: null, aderezoId: aderezoId, motivo);
        await _db.SaveChangesAsync(ct);
    }

    private void AddMovimiento(string negocioId, string pedidoId, double cantidad, double stockAntes, string tipo,
        string? insumoId, string? extraId, string? aderezoId, string motivo) =>
        _db.StockMovimientos.Add(new StockMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            InsumoId = insumoId,
            ExtraId = extraId,
            AderezoId = aderezoId,
            Tipo = tipo,
            Cantidad = -cantidad,
            StockAntes = stockAntes,
            StockDespues = stockAntes - cantidad,
            PedidoId = pedidoId,
            Motivo = motivo,
        });

    private void AddMovimientoReposicion(string negocioId, string pedidoId, double cantidad, double stockAntes,
        string? insumoId, string? extraId, string? aderezoId, string motivo) =>
        _db.StockMovimientos.Add(new StockMovimiento
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            InsumoId = insumoId,
            ExtraId = extraId,
            AderezoId = aderezoId,
            Tipo = TipoDevolucionCancelacion,
            Cantidad = +cantidad,
            StockAntes = stockAntes,
            StockDespues = stockAntes + cantidad,
            PedidoId = pedidoId,
            Motivo = motivo,
        });

    private async Task<ExtraData?> CargarExtraParaReponerAsync(string extraId, CancellationToken ct) =>
        await _db.Extras.AsNoTracking().Where(e => e.Id == extraId)
            .Select(e => new ExtraData(e.Id, e.Nombre, e.Precio, e.StockActual, e.Activo, e.EsPremium, e.InsumoId, e.ToppingGrupoId, null,
                new List<CategoriaValor>(),
                e.ExtraConsumos.Select(cc => new CategoriaValor(cc.CategoriaId, cc.CantidadConsumo)).ToList()))
            .FirstOrDefaultAsync(ct);

    private async Task<AderezoData?> CargarAderezoParaReponerAsync(string aderezoId, CancellationToken ct)
    {
        var ade = await _db.Aderezos.AsNoTracking().Where(a => a.Id == aderezoId)
            .Select(a => new { a.Id, a.Nombre, a.StockActual, a.Activo, a.EsPremium, a.Precio,
                Consumos = a.AderezoConsumos.Select(c => new CategoriaValor(c.CategoriaId, c.CantidadConsumo)).ToList() })
            .FirstOrDefaultAsync(ct);
        return ade is null ? null : new AderezoData(ade.Id, ade.Nombre, ade.StockActual, ade.Activo, ade.EsPremium, ade.Precio, ade.Consumos, null!);
    }

    private async Task UpsertClienteAsync(
        string telefono, string? nombre, string? apellido, double monto, string pedidoId, string negocioId, CancellationToken ct)
    {
        var now = Now();
        var cliente = await _db.Clientes.FirstOrDefaultAsync(c => c.Telefono == telefono, ct);
        if (cliente is null)
        {
            cliente = new Cliente
            {
                Id = Guid.NewGuid().ToString(),
                Nombre = nombre ?? telefono,
                Apellido = apellido,
                Telefono = telefono,
                TotalPedidos = 1,
                TotalGastado = monto,
                FechaUltimoPedido = now,
                NegocioId = negocioId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Clientes.Add(cliente);
        }
        else
        {
            cliente.TotalPedidos += 1;
            cliente.TotalGastado += monto;
            cliente.FechaUltimoPedido = now;
            cliente.UpdatedAt = now;
        }

        // Persistir el cliente ANTES de linkearlo: el FK pedido.clienteId se valida al cierre de cada
        // statement en Postgres, así que la fila del cliente debe existir antes del ExecuteUpdate.
        await _db.SaveChangesAsync(ct);
        await _db.Pedidos.Where(p => p.Id == pedidoId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClienteId, cliente.Id), ct);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Pricing helpers (paridad con getExtraPrecio/getExtraConsumo/getAderezoConsumo)
    // ═════════════════════════════════════════════════════════════════════════

    private static double GetExtraPrecio(ExtraData extra, string? categoriaId)
    {
        if (categoriaId is not null)
        {
            var esp = extra.PreciosPorCategoria.FirstOrDefault(p => p.CategoriaId == categoriaId);
            if (esp is not null) return esp.Valor;
        }
        return extra.Precio;
    }

    private static double GetExtraConsumo(ExtraData extra, string? categoriaId)
    {
        if (categoriaId is not null)
        {
            var esp = extra.ConsumosPorCategoria.FirstOrDefault(c => c.CategoriaId == categoriaId);
            if (esp is not null) return esp.Valor;
        }
        throw PedidoException.BadRequest($"El extra \"{extra.Nombre}\" no está configurado para la categoría del producto. Pedí al administrador que configure el consumo.");
    }

    private static double GetAderezoConsumo(AderezoData aderezo, string? categoriaId)
    {
        if (categoriaId is not null)
        {
            var esp = aderezo.ConsumosPorCategoria.FirstOrDefault(c => c.CategoriaId == categoriaId);
            if (esp is not null) return esp.Valor;
        }
        throw PedidoException.BadRequest($"El aderezo \"{aderezo.Nombre}\" no está configurado para la categoría del producto. Pedí al administrador que configure el consumo.");
    }

    private static bool PuedeAgregarItems(Pedido p) =>
        p.CuentaAbierta && p.EstadoPago == EstadoPago.PENDIENTE && p.Estado != EstadoPedido.CANCELADO && p.Tipo == TipoPedido.LOCAL;

    private static double RoundHalfUp(double value) => Math.Floor(value + 0.5);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static string? TrimOrNull(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    // ── Tipos internos de carga ──
    private sealed record RecetaItem(string InsumoId, double Cantidad);
    private sealed record ProductoData(string Id, double Precio, bool Activo, string Nombre, string? CategoriaId, List<RecetaItem> Receta);
    private sealed record CategoriaValor(string CategoriaId, double Valor);
    private sealed record ToppingGrupoData(int MaxExtrasGratis, bool EsIncluido);
    private sealed record ExtraData(
        string Id, string Nombre, double Precio, double StockActual, bool Activo, bool EsPremium, string? InsumoId,
        string? ToppingGrupoId, ToppingGrupoData? ToppingGrupo, List<CategoriaValor> PreciosPorCategoria, List<CategoriaValor> ConsumosPorCategoria);
    private sealed record AderezoData(
        string Id, string Nombre, double StockActual, bool Activo, bool EsPremium, double Precio,
        List<CategoriaValor> ConsumosPorCategoria, Aderezo Entidad);
}
