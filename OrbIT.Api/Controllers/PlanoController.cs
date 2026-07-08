using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Api.Billing;
using OrbIT.Api.Contracts.Plano;
using OrbIT.Application.Planes;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Plano de salón (editor drag &amp; drop): el layout de mesas/paredes/barras de un negocio. Es 1:1 con el
/// negocio (índice único sobre <c>negocioId</c>), así que no hay listado ni id en la ruta: siempre se opera
/// sobre "el plano del negocio activo", resuelto por el Global Query Filter del tenant.
///
/// Sólo ADMIN, y sólo si el plan habilita la feature de mesas (<see cref="PlanFeature.Mesas"/>) — el editor
/// del plano es la misma feature Pro-only que la gestión de mesas del <c>MesasController</c>. Los elementos
/// se guardan tal cual en la columna jsonb <c>elementos</c>; el estado visual (mesa libre/ocupada/sucia) lo
/// resuelve el front cruzando <c>ElementoPlano.MesaId</c> contra <c>GET /mesas</c>, no se persiste acá.
/// </summary>
[ApiController]
[Route("plano")]
[Authorize(Roles = "ADMIN")]
public sealed class PlanoController : ControllerBase
{
    private const string UniqueViolation = "23505";

    private const int DefaultCanvasWidth = 1200;
    private const int DefaultCanvasHeight = 800;

    private static readonly HashSet<string> TiposValidos = new(StringComparer.Ordinal) { "mesa", "pared", "barra" };
    private static readonly HashSet<string> FormasValidas = new(StringComparer.Ordinal) { "cuadrada", "rectangular", "recortada" };

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly IPlanGuard _planGuard;

    public PlanoController(OrbitDbContext db, ITenantProvider tenant, IPlanGuard planGuard)
    {
        _db = db;
        _tenant = tenant;
        _planGuard = planGuard;
    }

    /// <summary>
    /// Gate de plan del plano (feature Mesas, Pro-only), idéntico al del <c>MesasController</c>: devuelve el
    /// negocioId resuelto si el plan la habilita, o un 403 (feature) / Forbid (sin tenant) listo para retornar.
    /// </summary>
    private async Task<(string? NegocioId, IActionResult? Error)> GuardAsync()
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

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if ((await GuardAsync()).Error is { } guardError)
        {
            return guardError;
        }

        // El query filter acota al tenant activo; a lo sumo hay una fila (índice único por negocio).
        var plano = await _db.PlanoSalons.AsNoTracking().FirstOrDefaultAsync();
        return plano is null ? NotFound() : Ok(ToResponse(plano));
    }

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] SavePlanoRequest request)
    {
        var (negocioId, guardError) = await GuardAsync();
        if (guardError is not null)
        {
            return guardError;
        }

        if (ValidarElementos(request.Elementos) is { } validacionError)
        {
            return validacionError;
        }

        var now = Now();
        var plano = await _db.PlanoSalons.FirstOrDefaultAsync();
        var creando = plano is null;
        if (plano is null)
        {
            plano = new PlanoSalon
            {
                Id = Guid.NewGuid().ToString(),
                NegocioId = negocioId!, // no-null: GuardAsync devuelve error si falta el tenant.
                CreatedAt = now,
            };
            _db.PlanoSalons.Add(plano);
        }

        AplicarCambios(plano, request, now);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (creando && IsUniqueViolation(ex))
        {
            // Carrera contra el índice único (negocioId): otro request creó el plano primero. Recargamos el
            // ganador y aplicamos el upsert como update sobre esa fila.
            _db.Entry(plano).State = EntityState.Detached;
            var ganador = await _db.PlanoSalons.FirstOrDefaultAsync();
            if (ganador is null)
            {
                throw;
            }
            AplicarCambios(ganador, request, now);
            await _db.SaveChangesAsync();
            return Ok(ToResponse(ganador));
        }

        return Ok(ToResponse(plano));
    }

    [HttpDelete]
    public async Task<IActionResult> Reset()
    {
        if ((await GuardAsync()).Error is { } guardError)
        {
            return guardError;
        }

        var plano = await _db.PlanoSalons.FirstOrDefaultAsync();
        if (plano is null)
        {
            return NotFound();
        }

        // Reset = vaciar el layout (paridad con "borra todos los elementos"): se conserva la fila y las
        // dimensiones del canvas, sólo se limpia el array de elementos.
        plano.Elementos = new List<ElementoPlano>();
        plano.UpdatedAt = Now();
        await _db.SaveChangesAsync();
        return Ok(ToResponse(plano));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────

    private static void AplicarCambios(PlanoSalon plano, SavePlanoRequest request, DateTime now)
    {
        plano.Elementos = request.Elementos ?? new List<ElementoPlano>();
        // Canvas ≤ 0 (o body sin el campo) → defaults, en línea con los defaults de la columna en la DB.
        plano.CanvasWidth = request.CanvasWidth > 0 ? request.CanvasWidth : DefaultCanvasWidth;
        plano.CanvasHeight = request.CanvasHeight > 0 ? request.CanvasHeight : DefaultCanvasHeight;
        plano.UpdatedAt = now;
    }

    /// <summary>Valida tipo/forma de cada elemento (evita meter valores fuera del contrato en el jsonb).</summary>
    private BadRequestObjectResult? ValidarElementos(List<ElementoPlano>? elementos)
    {
        if (elementos is null)
        {
            return null;
        }
        foreach (var el in elementos)
        {
            if (string.IsNullOrWhiteSpace(el.Id))
            {
                return BadRequest(new { message = "Cada elemento del plano necesita un id." });
            }
            if (!TiposValidos.Contains(el.Tipo))
            {
                return BadRequest(new { message = $"Tipo de elemento inválido: '{el.Tipo}'. Esperado: mesa, pared o barra." });
            }
            if (!FormasValidas.Contains(el.Forma))
            {
                return BadRequest(new { message = $"Forma de elemento inválida: '{el.Forma}'. Esperado: cuadrada, rectangular o recortada." });
            }
        }
        return null;
    }

    private static PlanoResponse ToResponse(PlanoSalon p) =>
        new(p.Id, p.NegocioId, p.Elementos, p.CanvasWidth, p.CanvasHeight, p.CreatedAt, p.UpdatedAt);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
