using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Contracts.Clientes;
using OrbIT.Api.MultiTenancy;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// CRM básico del negocio (sin login de cliente final): alta/edición de clientes, búsqueda, estadísticas e
/// historial de pedidos. Todo scopeado por negocio (tenant) vía los Global Query Filters del
/// <c>OrbitDbContext</c>. Lectura y escritura para ADMIN y TRABAJADOR (paridad con NestJS).
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Upsert idempotente:</b> el flujo de pedidos hace <c>POST /clientes/upsert</c> (match por
///   teléfono). Ante carrera de concurrencia el INSERT puede reventar el índice único; se captura el
///   <c>23505</c>, se recarga el ganador y se aplica como update → un solo cliente, sin 500.</item>
///   <item><b>Duplicado de teléfono en el alta plana:</b> pre-chequeo → 409 (con backstop por <c>23505</c>),
///   en vez de reventar la unique constraint con un 500.</item>
///   <item><b>Paginación server-side</b> en el listado y en el historial de pedidos (<c>{ data, total }</c>),
///   con projection directa al DTO (no se trae la entidad completa desde Postgres).</item>
///   <item><b>Campos calculados</b> en el DTO sin columna ni query extra: <c>TicketPromedio</c> y
///   <c>EsClienteFrecuente</c>.</item>
///   <item><b>Guard de borrado:</b> la FK <c>Pedido.clienteId</c> es <c>ON DELETE SET NULL</c>, así que la
///   base NO bloquea el borrado. El guard (cliente con pedidos → 400) es 100% aplicativo, sin backstop.</item>
/// </list>
/// </summary>
[ApiController]
[Route("clientes")]
[Authorize(Roles = "ADMIN,TRABAJADOR")]
public sealed class ClientesController : ControllerBase
{
    // Código SQLSTATE de violación de unicidad en PostgreSQL (backstop del pre-chequeo de teléfono).
    private const string UniqueViolation = "23505";

    private const int DefaultPageSize = 50;
    private const int PedidosPageSize = 20;
    private const int MaxPageSize = 200;
    private const int PreviewPedidos = 20;
    private const int TopClientesCount = 10;
    private const int FrecuenteThreshold = 5;

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public ClientesController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // Projection reutilizable entidad → DTO. El ternario sobre TotalPedidos se traduce a un CASE WHEN en
    // Postgres (short-circuit), así que no hay división por cero cuando el cliente no tiene pedidos.
    private static readonly Expression<Func<Cliente, ClienteResponse>> ToResponseProjection = c => new ClienteResponse(
        c.Id, c.Nombre, c.Apellido, c.Telefono, c.DireccionFavorita,
        c.TotalPedidos, c.TotalGastado,
        c.TotalPedidos == 0 ? 0 : c.TotalGastado / c.TotalPedidos,
        c.TotalPedidos >= FrecuenteThreshold,
        c.FechaUltimoPedido, c.Notas, c.CreatedAt, c.UpdatedAt);

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? search = null,
        [FromQuery] int? minPedidos = null,
        [FromQuery] int page = 1,
        [FromQuery] int limit = DefaultPageSize)
    {
        (page, limit) = NormalizePaging(page, limit, DefaultPageSize);

        var query = _db.Clientes.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Nombre, term) ||
                (c.Apellido != null && EF.Functions.ILike(c.Apellido, term)) ||
                EF.Functions.ILike(c.Telefono, term));
        }

        if (minPedidos is { } min)
        {
            query = query.Where(c => c.TotalPedidos >= min);
        }

        var total = await query.CountAsync();
        var data = await query
            .OrderByDescending(c => c.TotalPedidos)
            .ThenBy(c => c.Nombre)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(ToResponseProjection)
            .ToListAsync();

        return Ok(new ClientesPageResponse(data, total));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var total = await _db.Clientes.CountAsync();
        var conMasDeUnPedido = await _db.Clientes.CountAsync(c => c.TotalPedidos > 1);
        var topClientes = await _db.Clientes.AsNoTracking()
            .OrderByDescending(c => c.TotalPedidos)
            .ThenBy(c => c.Nombre)
            .Take(TopClientesCount)
            .Select(ToResponseProjection)
            .ToListAsync();

        return Ok(new ClientesStatsResponse(total, conMasDeUnPedido, topClientes));
    }

    [HttpGet("telefono/{telefono}")]
    public async Task<IActionResult> GetByTelefono(string telefono)
    {
        var cliente = await _db.Clientes.AsNoTracking()
            .Where(c => c.Telefono == telefono)
            .Select(ToResponseProjection)
            .FirstOrDefaultAsync();
        return cliente is null ? NotFound() : Ok(cliente);
    }

    [HttpGet("{id}", Name = nameof(GetClienteById))]
    public async Task<IActionResult> GetClienteById(string id)
    {
        // El query filter garantiza la pertenencia al tenant (id ajeno → null → 404).
        var cliente = await _db.Clientes.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ClienteDetalleResponse(
                c.Id, c.Nombre, c.Apellido, c.Telefono, c.DireccionFavorita,
                c.TotalPedidos, c.TotalGastado,
                c.TotalPedidos == 0 ? 0 : c.TotalGastado / c.TotalPedidos,
                c.TotalPedidos >= FrecuenteThreshold,
                c.FechaUltimoPedido, c.Notas, c.CreatedAt, c.UpdatedAt,
                c.Pedidos
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(PreviewPedidos)
                    .Select(p => new PedidoPreviewResponse(p.Id, p.Estado, p.Total, p.Direccion, p.CreatedAt))
                    .ToList()))
            .FirstOrDefaultAsync();
        return cliente is null ? NotFound() : Ok(cliente);
    }

    [HttpGet("{id}/pedidos")]
    public async Task<IActionResult> GetPedidos(
        string id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = PedidosPageSize)
    {
        // Existencia scopeada al tenant: si el id no es de este negocio → 404 (no lista vacía silenciosa).
        if (!await _db.Clientes.AnyAsync(c => c.Id == id))
        {
            return NotFound();
        }

        (page, limit) = NormalizePaging(page, limit, PedidosPageSize);

        var query = _db.Pedidos.AsNoTracking().Where(p => p.ClienteId == id);
        var total = await query.CountAsync();
        var data = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(p => new PedidoPreviewResponse(p.Id, p.Estado, p.Total, p.Direccion, p.CreatedAt))
            .ToListAsync();

        return Ok(new PedidosPageResponse(data, total));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClienteRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var telefono = request.Telefono.Trim();
        if (telefono.Length == 0)
        {
            return BadRequest(new { message = "El teléfono es obligatorio." });
        }
        if (await _db.Clientes.AnyAsync(c => c.Telefono == telefono))
        {
            return TelefonoDuplicado(telefono);
        }

        var now = Now();
        var cliente = new Cliente
        {
            Id = Guid.NewGuid().ToString(),
            Nombre = request.Nombre.Trim(),
            Apellido = TrimOrNull(request.Apellido),
            Telefono = telefono,
            DireccionFavorita = TrimOrNull(request.DireccionFavorita),
            Notas = TrimOrNull(request.Notas),
            TotalPedidos = 0,
            TotalGastado = 0,
            FechaUltimoPedido = null,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Clientes.Add(cliente);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return TelefonoDuplicado(telefono);
        }

        return CreatedAtAction(nameof(GetClienteById), new { id = cliente.Id }, ToResponse(cliente));
    }

    [HttpPost("upsert")]
    public async Task<IActionResult> Upsert([FromBody] UpsertClienteRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var telefono = request.Telefono.Trim();
        if (telefono.Length == 0)
        {
            return BadRequest(new { message = "El teléfono es obligatorio." });
        }

        var now = Now();

        // Camino feliz: ya existe → actualizar (datos de contacto + acumulado de pedido si vino monto).
        var existente = await _db.Clientes.FirstOrDefaultAsync(c => c.Telefono == telefono);
        if (existente is not null)
        {
            AplicarUpsert(existente, request, now);
            await _db.SaveChangesAsync();
            return Ok(ToResponse(existente));
        }

        // No existe → crear.
        var nuevo = new Cliente
        {
            Id = Guid.NewGuid().ToString(),
            Telefono = telefono,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        AplicarUpsert(nuevo, request, now);
        _db.Clientes.Add(nuevo);

        try
        {
            await _db.SaveChangesAsync();
            return Ok(ToResponse(nuevo));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Carrera: otro request creó el mismo teléfono entre el SELECT y el INSERT. Para idempotencia
            // real recargamos al ganador y aplicamos el upsert como update (no se duplica el cliente).
            _db.Entry(nuevo).State = EntityState.Detached;
            var ganador = await _db.Clientes.FirstOrDefaultAsync(c => c.Telefono == telefono);
            if (ganador is null)
            {
                throw; // el 23505 implica que existe; si no aparece, algo más raro pasó.
            }
            AplicarUpsert(ganador, request, now);
            await _db.SaveChangesAsync();
            return Ok(ToResponse(ganador));
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateClienteRequest request)
    {
        var cliente = await _db.Clientes.FirstOrDefaultAsync(c => c.Id == id);
        if (cliente is null)
        {
            return NotFound();
        }

        if (request.Nombre is not null) cliente.Nombre = request.Nombre.Trim();
        if (request.Apellido is not null) cliente.Apellido = TrimOrNull(request.Apellido);
        if (request.DireccionFavorita is not null) cliente.DireccionFavorita = TrimOrNull(request.DireccionFavorita);
        if (request.Notas is not null) cliente.Notas = TrimOrNull(request.Notas);

        cliente.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(ToResponse(cliente));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var cliente = await _db.Clientes.FirstOrDefaultAsync(c => c.Id == id);
        if (cliente is null)
        {
            return NotFound();
        }

        // La FK Pedido.clienteId es ON DELETE SET NULL: la base no bloquea el borrado, así que el guard es
        // puramente aplicativo (no hay backstop de DB posible). Paridad funcional con NestJS.
        if (await _db.Pedidos.AnyAsync(p => p.ClienteId == id))
        {
            return BadRequest(new
            {
                message = "No se puede borrar un cliente con pedidos asociados. " +
                          "Se perdería el historial del CRM.",
            });
        }

        _db.Clientes.Remove(cliente);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aplica el body del upsert sobre un cliente (nuevo o existente). Los datos de contacto opcionales sólo
    /// pisan si vinieron (no se borran con un upsert parcial del flujo de pedidos). Si vino
    /// <c>MontoPedido</c>, acumula un pedido: <c>TotalPedidos++</c>, <c>TotalGastado += monto</c> y
    /// <c>FechaUltimoPedido = ahora</c>.
    /// </summary>
    private static void AplicarUpsert(Cliente c, UpsertClienteRequest request, DateTime now)
    {
        c.Nombre = request.Nombre.Trim();
        if (request.Apellido is not null) c.Apellido = TrimOrNull(request.Apellido);
        if (request.DireccionFavorita is not null) c.DireccionFavorita = TrimOrNull(request.DireccionFavorita);
        if (request.Notas is not null) c.Notas = TrimOrNull(request.Notas);

        if (request.MontoPedido is { } monto)
        {
            c.TotalPedidos += 1;
            c.TotalGastado += monto;
            c.FechaUltimoPedido = now;
        }

        c.UpdatedAt = now;
    }

    private static (int page, int limit) NormalizePaging(int page, int limit, int defaultLimit)
    {
        if (page < 1) page = 1;
        if (limit < 1) limit = defaultLimit;
        if (limit > MaxPageSize) limit = MaxPageSize;
        return (page, limit);
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

    private static ClienteResponse ToResponse(Cliente c) =>
        new(c.Id, c.Nombre, c.Apellido, c.Telefono, c.DireccionFavorita,
            c.TotalPedidos, c.TotalGastado,
            c.TotalPedidos == 0 ? 0 : c.TotalGastado / c.TotalPedidos,
            c.TotalPedidos >= FrecuenteThreshold,
            c.FechaUltimoPedido, c.Notas, c.CreatedAt, c.UpdatedAt);

    private ConflictObjectResult TelefonoDuplicado(string telefono) =>
        Conflict(new { message = $"Ya existe un cliente con el teléfono '{telefono}'." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
