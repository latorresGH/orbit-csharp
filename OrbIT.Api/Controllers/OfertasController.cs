using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Ofertas;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Ofertas;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRUD de ofertas + previsualización de descuentos, scopeado por negocio (tenant) vía los Global Query
/// Filters del <c>OrbitDbContext</c>. El cálculo en sí vive en <see cref="IOfertasCalculatorService"/>
/// (OrbIT.Application), reutilizable por el futuro PedidosController.
///
/// Roles: escritura ADMIN-only; detalle ADMIN/TRABAJADOR; <see cref="Listar"/> y <see cref="Calcular"/>
/// son del menú público (tenant por claim o <c>?negocio=slug</c>, ver <see cref="AllowAnonymousWithTenantAttribute"/>).
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Borrado con histórico:</b> si la oferta tiene <c>PedidoOferta</c> (FK <c>Restrict</c>) → 409
///   (no se destruye el historial); si no, borrado físico y el <b>CASCADE de la DB</b> limpia
///   productos/grupos/opciones (NestJS los borraba a mano).</item>
///   <item><b>Validación de productos cross-tenant:</b> los <c>productoId</c> de productos y opciones se
///   validan contra el tenant (estructural) — NestJS no lo hacía.</item>
///   <item><b>Duplicados/errores → 409</b> donde aplica, consistente con el resto del proyecto.</item>
/// </list>
/// </summary>
[ApiController]
[Route("ofertas")]
[Authorize]
public sealed class OfertasController : ControllerBase
{
    private const string ForeignKeyViolation = "23503";
    private const string DiasAplicablesDefault = "1,2,3,4,5,6,7";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IOfertasCalculatorService _calculator;

    public OfertasController(OrbitDbContext db, ITenantProvider tenant, IOfertasCalculatorService calculator)
    {
        _db = db;
        _tenant = tenant;
        _calculator = calculator;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura pública (menú) + cálculo.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> Listar([FromQuery] bool activas = false)
    {
        var query = _db.Oferta.AsNoTracking().AsQueryable();
        if (activas)
        {
            query = query.Where(o => o.Activa);
        }

        var ofertas = await query
            .OrderByDescending(o => o.CreatedAt)
            .Select(OfertaProjection)
            .ToListAsync();
        return Ok(ofertas);
    }

    [HttpPost("calcular")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> Calcular([FromBody] PreviewOfertaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var lineas = request.Lineas
            .Select(l => new LineaCalculo(l.ProductoId, l.Cantidad, l.PrecioUnitario ?? 0, l.MediaMedia ?? false))
            .ToList();

        var resultado = await _calculator.CalcularAsync(lineas, negocioId);
        return Ok(resultado);
    }

    [HttpGet("{id}", Name = nameof(GetOfertaById))]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> GetOfertaById(string id)
    {
        var oferta = await _db.Oferta.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(OfertaProjection)
            .FirstOrDefaultAsync();
        return oferta is null ? NotFound() : Ok(oferta);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Crear([FromBody] CreateOfertaRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        if (ValidarReglasDeTipo(request.Tipo, request.PorcentajeDescuento, request.MontoDescuento,
                request.GruposCombo, request.Productos) is { } tipoError)
        {
            return tipoError;
        }

        if (!TryParseFechaLocal(request.FechaInicio, out var fechaInicio))
        {
            return BadRequest(new { message = "fechaInicio inválida (formato esperado yyyy-MM-dd)." });
        }
        DateTime? fechaFin = null;
        if (request.FechaFin is not null)
        {
            if (!TryParseFechaLocal(request.FechaFin, out var ff))
            {
                return BadRequest(new { message = "fechaFin inválida (formato esperado yyyy-MM-dd)." });
            }
            if (ff <= fechaInicio)
            {
                return BadRequest(new { message = "La fecha de fin debe ser posterior a la fecha de inicio." });
            }
            fechaFin = ff;
        }

        if (await ProductosFueraDelTenant(request.Productos, request.GruposCombo) is { } prodError)
        {
            return prodError;
        }

        var now = Now();
        var oferta = new Ofertum
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = request.Nombre.Trim(),
            Descripcion = TrimOrNull(request.Descripcion),
            Tipo = request.Tipo,
            // Defaults explícitos (sentinel de EF: con HasDefaultValue/Sql el CLR default no se respeta solo).
            Estado = request.Estado ?? EstadoOferta.ACTIVA,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin,
            Activa = request.Activa ?? true,
            PorcentajeDescuento = request.PorcentajeDescuento,
            MontoDescuento = request.MontoDescuento,
            MaxUsosPorCliente = request.MaxUsosPorCliente,
            MaxUsosTotales = request.MaxUsosTotales,
            DiasAplicables = string.IsNullOrWhiteSpace(request.DiasAplicables) ? DiasAplicablesDefault : request.DiasAplicables.Trim(),
            HoraInicio = TrimOrNull(request.HoraInicio),
            HoraFin = TrimOrNull(request.HoraFin),
            AplicaPorLinea = request.AplicaPorLinea ?? true,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        AgregarProductos(oferta, request.Productos, negocioId);
        AgregarGruposCombo(oferta, request.GruposCombo, negocioId);

        _db.Oferta.Add(oferta);
        await _db.SaveChangesAsync();

        var response = await _db.Oferta.AsNoTracking().Where(o => o.Id == oferta.Id).Select(OfertaProjection).FirstAsync();
        return CreatedAtAction(nameof(GetOfertaById), new { id = oferta.Id }, response);
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Actualizar(string id, [FromBody] UpdateOfertaRequest request)
    {
        var oferta = await _db.Oferta.FirstOrDefaultAsync(o => o.Id == id);
        if (oferta is null)
        {
            return NotFound();
        }

        var tipoFinal = request.Tipo ?? oferta.Tipo;
        if (tipoFinal == TipoOferta.COMBO)
        {
            var montoFinal = request.MontoDescuento ?? oferta.MontoDescuento;
            var tieneMonto = montoFinal is > 0;
            var tienePrecioEspecial = request.Productos?.Any(p => p.PrecioEspecial is > 0) ?? false;
            if (!tieneMonto && !tienePrecioEspecial)
            {
                return BadRequest(new { message = "El combo debe tener un precio especial configurado." });
            }
        }

        var fechaInicio = oferta.FechaInicio;
        if (request.FechaInicio is not null)
        {
            if (!TryParseFechaLocal(request.FechaInicio, out fechaInicio))
            {
                return BadRequest(new { message = "fechaInicio inválida (formato esperado yyyy-MM-dd)." });
            }
        }
        var fechaFin = oferta.FechaFin;
        if (request.FechaFin is not null)
        {
            if (!TryParseFechaLocal(request.FechaFin, out var ff))
            {
                return BadRequest(new { message = "fechaFin inválida (formato esperado yyyy-MM-dd)." });
            }
            fechaFin = ff;
        }
        if (fechaFin is { } fin && fin <= fechaInicio)
        {
            return BadRequest(new { message = "La fecha de fin debe ser posterior a la fecha de inicio." });
        }

        if (await ProductosFueraDelTenant(request.Productos, request.GruposCombo) is { } prodError)
        {
            return prodError;
        }

        if (request.Nombre is not null) oferta.Nombre = request.Nombre.Trim();
        if (request.Descripcion is not null) oferta.Descripcion = TrimOrNull(request.Descripcion);
        if (request.Tipo is { } tipo) oferta.Tipo = tipo;
        if (request.Estado is { } estado) oferta.Estado = estado;
        oferta.FechaInicio = fechaInicio;
        oferta.FechaFin = fechaFin;
        if (request.Activa is { } activa) oferta.Activa = activa;
        if (request.PorcentajeDescuento is { } pct) oferta.PorcentajeDescuento = pct;
        if (request.MontoDescuento is { } monto) oferta.MontoDescuento = monto;
        if (request.MaxUsosPorCliente is { } mupc) oferta.MaxUsosPorCliente = mupc;
        if (request.MaxUsosTotales is { } mut) oferta.MaxUsosTotales = mut;
        if (request.DiasAplicables is not null) oferta.DiasAplicables = request.DiasAplicables.Trim();
        if (request.HoraInicio is not null) oferta.HoraInicio = TrimOrNull(request.HoraInicio);
        if (request.HoraFin is not null) oferta.HoraFin = TrimOrNull(request.HoraFin);
        if (request.AplicaPorLinea is { } apl) oferta.AplicaPorLinea = apl;
        oferta.UpdatedAt = Now();

        // Reemplazo total de productos / grupos si vinieron (aun vacíos). El CASCADE de la DB limpia las
        // GrupoOpcion al borrar sus GrupoCombo.
        if (request.Productos is not null)
        {
            var existentes = await _db.OfertaProductos.Where(p => p.OfertaId == id).ToListAsync();
            _db.OfertaProductos.RemoveRange(existentes);
            AgregarProductos(oferta, request.Productos, oferta.NegocioId);
        }
        if (request.GruposCombo is not null)
        {
            var grupos = await _db.GrupoCombos.Where(g => g.OfertaId == id).ToListAsync();
            _db.GrupoCombos.RemoveRange(grupos);
            AgregarGruposCombo(oferta, request.GruposCombo, oferta.NegocioId);
        }

        await _db.SaveChangesAsync();

        var response = await _db.Oferta.AsNoTracking().Where(o => o.Id == id).Select(OfertaProjection).FirstAsync();
        return Ok(response);
    }

    [HttpPatch("{id}/activa")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> SetActiva(string id, [FromBody] SetOfertaActivaRequest request)
    {
        var oferta = await _db.Oferta.FirstOrDefaultAsync(o => o.Id == id);
        if (oferta is null)
        {
            return NotFound();
        }

        oferta.Activa = request.Activa;
        oferta.UpdatedAt = Now();
        await _db.SaveChangesAsync();

        var response = await _db.Oferta.AsNoTracking().Where(o => o.Id == id).Select(OfertaProjection).FirstAsync();
        return Ok(response);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Eliminar(string id)
    {
        var oferta = await _db.Oferta.FirstOrDefaultAsync(o => o.Id == id);
        if (oferta is null)
        {
            return NotFound();
        }

        // No destruir el histórico: si la oferta fue aplicada a algún pedido (FK Restrict) → 409.
        if (await _db.PedidoOferta.AnyAsync(po => po.OfertaId == id))
        {
            return Conflict(new
            {
                message = "No se puede borrar una oferta aplicada a pedidos. Pausala (activa=false) en su lugar.",
            });
        }

        // El CASCADE de la DB borra OfertaProducto/GrupoCombo/GrupoOpcion al borrar la oferta.
        _db.Oferta.Remove(oferta);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: ForeignKeyViolation })
        {
            return Conflict(new { message = "No se puede borrar una oferta aplicada a pedidos." });
        }

        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Validaciones de coherencia por tipo de oferta (paridad con NestJS).</summary>
    private BadRequestObjectResult? ValidarReglasDeTipo(
        TipoOferta tipo, double? porcentaje, double? monto,
        List<GrupoComboRequest>? grupos, List<OfertaProductoRequest>? productos)
    {
        if (tipo == TipoOferta.DESCUENTO_PORCENTAJE && porcentaje is null or 0)
        {
            return BadRequest(new { message = "porcentajeDescuento es requerido para DESCUENTO_PORCENTAJE." });
        }
        if (tipo == TipoOferta.DESCUENTO_MONTO_FIJO && monto is null or 0)
        {
            return BadRequest(new { message = "montoDescuento es requerido para DESCUENTO_MONTO_FIJO." });
        }
        if (tipo == TipoOferta.COMBO && (grupos is null || grupos.Count == 0))
        {
            return BadRequest(new { message = "gruposCombo es requerido para COMBO." });
        }
        if (tipo == TipoOferta.COMBO)
        {
            var tieneMonto = monto is > 0;
            var tienePrecioEspecial = productos?.Any(p => p.PrecioEspecial is > 0) ?? false;
            if (!tieneMonto && !tienePrecioEspecial)
            {
                return BadRequest(new { message = "El combo debe tener un precio especial configurado." });
            }
        }
        return null;
    }

    /// <summary>
    /// Verifica que todos los <c>productoId</c> (de productos y opciones de grupo) pertenezcan al tenant.
    /// Estructural: la query pasa por el Global Query Filter, así que ids ajenos/inexistentes no aparecen.
    /// </summary>
    private async Task<BadRequestObjectResult?> ProductosFueraDelTenant(
        List<OfertaProductoRequest>? productos, List<GrupoComboRequest>? grupos)
    {
        var ids = new HashSet<string>();
        if (productos is not null)
        {
            foreach (var p in productos) ids.Add(p.ProductoId);
        }
        if (grupos is not null)
        {
            foreach (var g in grupos)
            {
                foreach (var op in g.Opciones) ids.Add(op.ProductoId);
            }
        }
        if (ids.Count == 0)
        {
            return null;
        }

        var encontrados = await _db.Productos.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync();
        if (encontrados.Count != ids.Count)
        {
            return BadRequest(new { message = "Uno o más productos no existen o no pertenecen a este negocio." });
        }
        return null;
    }

    private static void AgregarProductos(Ofertum oferta, List<OfertaProductoRequest>? productos, string negocioId)
    {
        if (productos is null)
        {
            return;
        }
        foreach (var p in productos)
        {
            oferta.OfertaProductos.Add(new OfertaProducto
            {
                Id = Guid.NewGuid().ToString(),
                ProductoId = p.ProductoId,
                Obligatorio = p.Obligatorio ?? false,
                CantidadMin = p.CantidadMin ?? 1,
                CantidadMax = p.CantidadMax,
                PrecioEspecial = p.PrecioEspecial,
                NegocioId = negocioId,
            });
        }
    }

    private static void AgregarGruposCombo(Ofertum oferta, List<GrupoComboRequest>? grupos, string negocioId)
    {
        if (grupos is null)
        {
            return;
        }
        foreach (var g in grupos)
        {
            var grupo = new GrupoCombo
            {
                Id = Guid.NewGuid().ToString(),
                Nombre = g.Nombre.Trim(),
                Obligatorio = g.Obligatorio ?? true,
                Cantidad = g.Cantidad,
                NegocioId = negocioId,
            };
            foreach (var op in g.Opciones)
            {
                grupo.GrupoOpcions.Add(new GrupoOpcion
                {
                    Id = Guid.NewGuid().ToString(),
                    ProductoId = op.ProductoId,
                    NegocioId = negocioId,
                });
            }
            oferta.GrupoCombos.Add(grupo);
        }
    }

    private static bool TryParseFechaLocal(string value, out DateTime fecha)
    {
        // Acepta "yyyy-MM-dd" (o un ISO más largo, del que toma la parte de fecha), igual que parseFechaLocal.
        var datePart = value.Length >= 10 ? value[..10] : value;
        if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            fecha = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
            return true;
        }
        fecha = default;
        return false;
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static string? TrimOrNull(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // Projection entidad → DTO (incluye nombres de producto vía navegación; solo para responses, no para el cálculo).
    private static readonly System.Linq.Expressions.Expression<Func<Ofertum, OfertaResponse>> OfertaProjection = o => new OfertaResponse(
        o.Id, o.Nombre, o.Descripcion, o.Tipo, o.Estado, o.FechaInicio, o.FechaFin, o.Activa,
        o.PorcentajeDescuento, o.MontoDescuento, o.MaxUsosPorCliente, o.MaxUsosTotales, o.UsosActuales,
        o.DiasAplicables, o.HoraInicio, o.HoraFin, o.AplicaPorLinea, o.CreatedAt, o.UpdatedAt,
        o.OfertaProductos
            .Select(p => new OfertaProductoResponse(
                p.Id, p.ProductoId, p.Producto.Nombre, p.Obligatorio, p.CantidadMin, p.CantidadMax, p.PrecioEspecial))
            .ToList(),
        o.GrupoCombos
            .Select(g => new GrupoComboResponse(
                g.Id, g.Nombre, g.Obligatorio, g.Cantidad,
                g.GrupoOpcions.Select(op => new GrupoOpcionResponse(op.Id, op.ProductoId, op.Producto.Nombre)).ToList()))
            .ToList());
}
