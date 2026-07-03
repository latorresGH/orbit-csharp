using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Billing;
using OrbIT.Api.Contracts.Mesas;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Planes;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Tablero de salón: mesas y su grilla, scopeado por negocio (tenant) vía los Global Query Filters del
/// <c>OrbitDbContext</c>. NO es storefront — no hay endpoints públicos. Lectura y cambio de estado/liberar
/// para ADMIN+TRABAJADOR; alta/edición estructural, grilla y baja para ADMIN.
///
/// Relación con Pedidos (FK circular, ambas nullable + ON DELETE SET NULL):
/// <list type="bullet">
///   <item><c>Mesa.PedidoActivoId → Pedido</c> (one-to-one, índice UNIQUE): la "cuenta abierta" de la mesa.</item>
///   <item><c>Pedido.MesaId → Mesa</c> (many-to-one): el historial de pedidos de la mesa.</item>
/// </list>
/// El acople real (ocupar al crear el pedido, liberar al cerrarlo) lo maneja el módulo de Pedidos; acá el
/// estado se cambia a mano (<c>PATCH /{id}/estado</c>, <c>POST /{id}/liberar</c>).
///
/// Paridad y mejoras respecto al NestJS de producción:
/// <list type="bullet">
///   <item><b>Número duplicado:</b> NestJS devolvía 400; acá 409 (con backstop <c>23505</c>) por consistencia.</item>
///   <item><b>Hardening de cuenta abierta:</b> al ocupar una mesa que ya tiene OTRO pedido activo → 400
///   ("La mesa ya tiene una cuenta abierta"); si el pedido ya está activo en otra mesa, el índice UNIQUE de
///   <c>pedidoActivoId</c> revienta y se captura como 409. NestJS sobrescribía en silencio.</item>
///   <item><b>DELETE = baja lógica</b> (<c>activa=false</c>), paridad NestJS: las mesas tienen historial de
///   pedidos; borrarlas físicamente los orfanaría. Guard: no se puede dar de baja una mesa OCUPADA.</item>
///   <item><b>Grilla en 1 query</b> (en vez de los 2 <c>findUnique</c> de NestJS) y <b>projection liviana</b>
///   en el tablero (conteo de ítems correlacionado, sin traer los detalles del pedido activo).</item>
/// </list>
/// </summary>
[ApiController]
[Route("mesas")]
[Authorize(Roles = "ADMIN,TRABAJADOR")]
public sealed class MesasController : ControllerBase
{
    // Códigos SQLSTATE de PostgreSQL: unicidad (backstop de número/pedido activo) y FK (pedido inexistente).
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private const string GridColsKey = "mesas_grid_cols";
    private const string GridRowsKey = "mesas_grid_rows";
    private const int DefaultGridCols = 4;
    private const int DefaultGridRows = 3;

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IPlanGuard _planGuard;

    public MesasController(OrbitDbContext db, ITenantProvider tenant, IPlanGuard planGuard)
    {
        _db = db;
        _tenant = tenant;
        _planGuard = planGuard;
    }

    /// <summary>
    /// Gate de plan para la gestión de mesas (feature Pro-only). Devuelve el negocioId resuelto si el plan la
    /// habilita; si no, un 403 (feature) o 403/Forbid (sin tenant) listo para retornar desde la acción.
    /// </summary>
    private async Task<(string? NegocioId, IActionResult? Error)> GuardMesasAsync()
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return (null, Forbid());
        }
        if (!await _planGuard.VerificarFeatureAsync(negocioId, PlanFeature.Mesas))
        {
            return (null, PlanGuardResponses.Feature());
        }
        return (negocioId, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lectura: ADMIN / TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // Tablero: sólo mesas activas, con un resumen liviano del pedido activo (conteo de ítems
        // correlacionado, sin traer los PedidoDetalle completos → evita el over-fetching).
        var mesas = await _db.Mesas.AsNoTracking()
            .Where(m => m.Activa)
            .OrderBy(m => m.PosX).ThenBy(m => m.PosY)
            .Select(m => new MesaTableroResponse(
                m.Id, m.Numero, m.Nombre, m.Estado, m.Capacidad, m.Activa, m.PosX, m.PosY,
                m.PedidoActivo == null
                    ? null
                    : new PedidoActivoResumen(
                        m.PedidoActivo.Id, m.PedidoActivo.Total, m.PedidoActivo.Estado,
                        m.PedidoActivo.CuentaAbierta, m.PedidoActivo.PedidoDetalles.Count(),
                        m.PedidoActivo.CreatedAt)))
            .ToListAsync();
        return Ok(mesas);
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetGridConfig()
    {
        var grid = await ResolveGridConfigAsync();
        return Ok(grid);
    }

    [HttpGet("{id}", Name = nameof(GetMesaById))]
    public async Task<IActionResult> GetMesaById(string id)
    {
        // El query filter garantiza la pertenencia al tenant (id ajeno → null → 404).
        var mesa = await _db.Mesas.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new MesaDetalleResponse(
                m.Id, m.Numero, m.Nombre, m.Estado, m.Capacidad, m.Activa, m.PosX, m.PosY,
                m.CreatedAt, m.UpdatedAt,
                m.PedidoActivo == null
                    ? null
                    : new PedidoActivoDetalle(
                        m.PedidoActivo.Id, m.PedidoActivo.Total, m.PedidoActivo.Estado,
                        m.PedidoActivo.CuentaAbierta,
                        m.PedidoActivo.PedidoDetalles
                            .Select(d => new PedidoActivoItem(d.Id, d.Cantidad, d.Subtotal, d.Producto.Nombre))
                            .ToList())))
            .FirstOrDefaultAsync();
        return mesa is null ? NotFound() : Ok(mesa);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escritura estructural: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Create([FromBody] CreateMesaRequest request)
    {
        var (negocioId, guardError) = await GuardMesasAsync();
        if (guardError is not null)
        {
            return guardError;
        }

        if (await _db.Mesas.AnyAsync(m => m.Numero == request.Numero))
        {
            return NumeroDuplicado(request.Numero);
        }

        var grid = await ResolveGridConfigAsync();
        if (PosFueraDeGrilla(request.PosX, request.PosY, grid) is { } error)
        {
            return error;
        }

        var now = Now();
        var mesa = new Mesa
        {
            Id = Guid.NewGuid().ToString(),
            Numero = request.Numero,
            Nombre = TrimOrNull(request.Nombre),
            NegocioId = negocioId!, // no-null garantizado: GuardMesasAsync devuelve error no-null si falta el tenant.

            CreatedAt = now,
            UpdatedAt = now,
        };
        // Seteamos los defaults EXPLÍCITAMENTE (no confiar en el default de DB en el insert): con
        // HasDefaultValue(true)/(4) el sentinel de EF pasa a ser ese valor, así que dejar el CLR default
        // (false/0) haría que EF inserte false/0 en vez de tomar el default → la entidad en memoria que
        // devolvemos quedaría desincronizada. Ver [[ef-enum-sentinel-warnings]]. (4 replica el default DB.)
        mesa.Capacidad = request.Capacidad ?? 4;
        mesa.PosX = request.PosX ?? 0;
        mesa.PosY = request.PosY ?? 0;
        mesa.Activa = request.Activa ?? true;
        mesa.Estado = EstadoMesa.LIBRE;

        _db.Mesas.Add(mesa);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NumeroDuplicado(request.Numero);
        }

        return CreatedAtAction(nameof(GetMesaById), new { id = mesa.Id }, ToResponse(mesa));
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateMesaRequest request)
    {
        if ((await GuardMesasAsync()).Error is { } guardError)
        {
            return guardError;
        }

        var mesa = await _db.Mesas.FirstOrDefaultAsync(m => m.Id == id);
        if (mesa is null)
        {
            return NotFound();
        }

        if (request.Numero is { } numero && numero != mesa.Numero)
        {
            if (await _db.Mesas.AnyAsync(m => m.Numero == numero && m.Id != id))
            {
                return NumeroDuplicado(numero);
            }
            mesa.Numero = numero;
        }

        var grid = await ResolveGridConfigAsync();
        if (PosFueraDeGrilla(request.PosX, request.PosY, grid) is { } error)
        {
            return error;
        }

        if (request.Nombre is not null) mesa.Nombre = TrimOrNull(request.Nombre);
        if (request.Capacidad is { } capacidad) mesa.Capacidad = capacidad;
        if (request.Estado is { } estado) mesa.Estado = estado;
        if (request.Activa is { } activa) mesa.Activa = activa;
        if (request.PosX is { } posX) mesa.PosX = posX;
        if (request.PosY is { } posY) mesa.PosY = posY;

        mesa.UpdatedAt = Now();
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NumeroDuplicado(request.Numero!.Value);
        }

        return Ok(ToResponse(mesa));
    }

    [HttpPatch("config")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateGridConfig([FromBody] UpdateGridConfigRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var now = Now();
        await UpsertConfigAsync(GridColsKey, request.Cols.ToString(), "Columnas de la grilla de mesas", negocioId, now);
        await UpsertConfigAsync(GridRowsKey, request.Rows.ToString(), "Filas de la grilla de mesas", negocioId, now);

        return Ok(new GridConfigResponse(request.Cols, request.Rows));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Estado / liberar: ADMIN + TRABAJADOR.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpPatch("{id}/estado")]
    public async Task<IActionResult> UpdateEstado(string id, [FromBody] UpdateMesaEstadoRequest request)
    {
        var mesa = await _db.Mesas.FirstOrDefaultAsync(m => m.Id == id);
        if (mesa is null)
        {
            return NotFound();
        }

        var pedidoId = string.IsNullOrWhiteSpace(request.PedidoActivoId) ? null : request.PedidoActivoId.Trim();

        if (request.Estado == EstadoMesa.OCUPADA && pedidoId is null)
        {
            return BadRequest(new { message = "Se requiere un pedidoActivoId para ocupar una mesa." });
        }
        if (request.Estado == EstadoMesa.LIBRE && mesa.PedidoActivoId is not null)
        {
            return BadRequest(new { message = "La mesa tiene un pedido activo. Cerralo primero." });
        }

        // Hardening (desviación deliberada de NestJS): no pisar en silencio una cuenta abierta. Si la mesa ya
        // tiene OTRO pedido activo distinto al que se quiere asignar → 400.
        if (pedidoId is not null && mesa.PedidoActivoId is not null && mesa.PedidoActivoId != pedidoId)
        {
            return BadRequest(new { message = "La mesa ya tiene una cuenta abierta." });
        }

        mesa.Estado = request.Estado;
        // Sigue a NestJS: el pedido provisto gana; si no vino, se conserva el actual (para limpiar usar /liberar).
        mesa.PedidoActivoId = pedidoId ?? mesa.PedidoActivoId;
        mesa.UpdatedAt = Now();

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // El índice UNIQUE de pedidoActivoId: ese pedido ya está activo en otra mesa.
            return Conflict(new { message = "Ese pedido ya está activo en otra mesa." });
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            // El pedido asignado no existe (FK Mesa_pedidoActivoId_fkey).
            return BadRequest(new { message = "El pedido indicado no existe." });
        }

        return Ok(ToResponse(mesa));
    }

    [HttpPost("{id}/liberar")]
    public async Task<IActionResult> Liberar(string id)
    {
        var mesa = await _db.Mesas.FirstOrDefaultAsync(m => m.Id == id);
        if (mesa is null)
        {
            return NotFound();
        }
        if (mesa.Estado == EstadoMesa.LIBRE)
        {
            return BadRequest(new { message = "La mesa ya está libre." });
        }

        mesa.Estado = EstadoMesa.LIBRE;
        mesa.PedidoActivoId = null;
        mesa.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(ToResponse(mesa));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Baja lógica: ADMIN.
    // ─────────────────────────────────────────────────────────────────────────

    [HttpDelete("{id}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Delete(string id)
    {
        if ((await GuardMesasAsync()).Error is { } guardError)
        {
            return guardError;
        }

        var mesa = await _db.Mesas.FirstOrDefaultAsync(m => m.Id == id);
        if (mesa is null)
        {
            return NotFound();
        }
        if (mesa.Estado == EstadoMesa.OCUPADA)
        {
            return BadRequest(new { message = "No se puede eliminar una mesa ocupada." });
        }

        // Baja lógica (paridad NestJS): la mesa conserva su historial de pedidos (FK SET NULL); el borrado
        // físico los dejaría huérfanos. Por eso DELETE devuelve la mesa desactivada, no 204.
        mesa.Activa = false;
        mesa.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(ToResponse(mesa));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuelve cols/rows de la grilla en UNA sola query (mejora sobre los 2 <c>findUnique</c> de NestJS).
    /// Si falta la clave, usa el default (4×3). Tenant-scopeado por el query filter de Configuracion.
    /// </summary>
    private async Task<GridConfigResponse> ResolveGridConfigAsync()
    {
        var configs = await _db.Configuracions.AsNoTracking()
            .Where(c => c.Clave == GridColsKey || c.Clave == GridRowsKey)
            .Select(c => new { c.Clave, c.Valor })
            .ToListAsync();

        var cols = ParseOr(configs.FirstOrDefault(c => c.Clave == GridColsKey)?.Valor, DefaultGridCols);
        var rows = ParseOr(configs.FirstOrDefault(c => c.Clave == GridRowsKey)?.Valor, DefaultGridRows);
        return new GridConfigResponse(cols, rows);
    }

    private async Task UpsertConfigAsync(string clave, string valor, string descripcion, string negocioId, DateTime now)
    {
        var existente = await _db.Configuracions.FirstOrDefaultAsync(c => c.Clave == clave);
        if (existente is not null)
        {
            existente.Valor = valor;
            existente.UpdatedAt = now;
            await _db.SaveChangesAsync();
            return;
        }

        var nuevo = new Configuracion
        {
            Id = Guid.NewGuid().ToString(),
            Clave = clave,
            Valor = valor,
            Descripcion = descripcion,
            NegocioId = negocioId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Configuracions.Add(nuevo);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Carrera contra el índice (clave, negocioId): recargar el ganador y aplicar como update.
            _db.Entry(nuevo).State = EntityState.Detached;
            var ganador = await _db.Configuracions.FirstOrDefaultAsync(c => c.Clave == clave);
            if (ganador is null)
            {
                throw;
            }
            ganador.Valor = valor;
            ganador.UpdatedAt = now;
            await _db.SaveChangesAsync();
        }
    }

    private BadRequestObjectResult? PosFueraDeGrilla(int? posX, int? posY, GridConfigResponse grid)
    {
        if (posX is { } x && x >= grid.Cols)
        {
            return BadRequest(new { message = $"posX ({x}) excede las columnas de la grilla ({grid.Cols})." });
        }
        if (posY is { } y && y >= grid.Rows)
        {
            return BadRequest(new { message = $"posY ({y}) excede las filas de la grilla ({grid.Rows})." });
        }
        return null;
    }

    private static int ParseOr(string? valor, int fallback) =>
        int.TryParse(valor, out var parsed) ? parsed : fallback;

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

    private static MesaResponse ToResponse(Mesa m) =>
        new(m.Id, m.Numero, m.Nombre, m.Estado, m.Capacidad, m.Activa, m.PosX, m.PosY,
            m.PedidoActivoId, m.CreatedAt, m.UpdatedAt);

    private ConflictObjectResult NumeroDuplicado(int numero) =>
        Conflict(new { message = $"Ya existe una mesa con el número {numero}." });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: ForeignKeyViolation };
}
