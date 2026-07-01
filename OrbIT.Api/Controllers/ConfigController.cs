using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrbIT.Api.Contracts.Config;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Common;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Configuración clave/valor por negocio. Claves semi-libres: cualquier clave se puede setear, pero un
/// conjunto conocido tiene validadores (<see cref="Validadores"/>) que replican los del NestJS
/// (<c>config.controller.ts</c>). El listado completo y el estado de horario son públicos de menú vía
/// <see cref="AllowAnonymousWithTenantAttribute"/>; leer/escribir claves sueltas requiere sesión.
///
/// El cálculo de "¿está abierto?" reusa <see cref="HorarioComercial"/> (port del <c>calcularEstadoLocal</c>
/// de NestJS) y <see cref="ArgentinaClock"/>.
/// </summary>
[ApiController]
[Route("config")]
[Authorize]
public sealed class ConfigController : ControllerBase
{
    /// <summary>
    /// Validadores por clave (paridad con el NestJS). Clave sin validador → se acepta cualquier valor.
    /// </summary>
    private static readonly Dictionary<string, Func<string, bool>> Validadores = new(StringComparer.Ordinal)
    {
        ["hora_apertura"] = EsHoraHhMm,
        ["hora_cierre"] = EsHoraHhMm,
        ["mesas_grid_cols"] = v => EsEnteroEnRango(v, 1, 20),
        ["mesas_grid_rows"] = v => EsEnteroEnRango(v, 1, 20),
        ["delivery_precio_base"] = v => Regex.IsMatch(v, @"^\d+$") && int.TryParse(v, out var n) && n >= 0,
        ["stock_min_unidad"] = EsFloatNoNegativo,
        ["stock_min_gramo"] = EsFloatNoNegativo,
        ["stock_min_kilogramo"] = EsFloatNoNegativo,
        ["stock_min_mililitro"] = EsFloatNoNegativo,
        ["stock_min_litro"] = EsFloatNoNegativo,
        ["stock_min_pote"] = EsFloatNoNegativo,
        ["stock_min_sobre"] = EsFloatNoNegativo,
        ["stock_min_feta"] = EsFloatNoNegativo,
        ["modo_mesas"] = v => v is "true" or "false",
        ["dias_atencion"] = EsDiasAtencion,
    };

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;

    public ConfigController(OrbitDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Público de menú
    // ═════════════════════════════════════════════════════════════════════════

    [HttpGet]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> ObtenerTodas()
    {
        var configs = await _db.Configuracions.AsNoTracking()
            .OrderBy(c => c.Clave)
            .Select(c => new ConfigItemResponse(c.Id, c.Clave, c.Valor, c.Descripcion, c.CreatedAt, c.UpdatedAt))
            .ToListAsync();
        return Ok(configs);
    }

    /// <summary>
    /// ¿Está abierto el local ahora? Sin horas configuradas → abierto por defecto (paridad con NestJS).
    /// Público de menú.
    /// </summary>
    [HttpGet("horario/abierto")]
    [AllowAnonymousWithTenant]
    public async Task<IActionResult> HorarioAbierto()
    {
        var claves = await _db.Configuracions.AsNoTracking()
            .Where(c => c.Clave == "hora_apertura" || c.Clave == "hora_cierre" || c.Clave == "dias_atencion")
            .ToDictionaryAsync(c => c.Clave, c => c.Valor);

        var horaActual = ArgentinaClock.HoraHhMm(ArgentinaClock.Now());
        claves.TryGetValue("hora_apertura", out var horaApertura);
        claves.TryGetValue("hora_cierre", out var horaCierre);

        if (string.IsNullOrEmpty(horaApertura) || string.IsNullOrEmpty(horaCierre))
        {
            return Ok(new HorarioAbiertoResponse(
                true, null, null, horaActual, new[] { 1, 2, 3, 4, 5, 6, 7 }, "abierto", null));
        }

        var diasAtencion = ParseDiasAtencion(claves.GetValueOrDefault("dias_atencion"));
        var estado = HorarioComercial.CalcularEstado(horaApertura, horaCierre, diasAtencion);

        var proxima = estado.ProximaApertura is { } pa
            ? new ProximaAperturaResponse(pa.Dia, pa.Hora, HorarioComercial.NombresDias.GetValueOrDefault(pa.Dia, ""))
            : null;

        return Ok(new HorarioAbiertoResponse(
            estado.Abierto, horaApertura, horaCierre, horaActual, diasAtencion, RazonToString(estado.Razon), proxima));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Con sesión
    // ═════════════════════════════════════════════════════════════════════════

    [HttpGet("{clave}")]
    [Authorize(Roles = "ADMIN,TRABAJADOR")]
    public async Task<IActionResult> Obtener(string clave)
    {
        var valor = await _db.Configuracions.AsNoTracking()
            .Where(c => c.Clave == clave)
            .Select(c => c.Valor)
            .FirstOrDefaultAsync();
        return Ok(new ConfigValorResponse(clave, valor));
    }

    [HttpPost("{clave}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Establecer(string clave, [FromBody] SetConfigRequest request)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId))
        {
            return Forbid();
        }

        var valor = request.Valor ?? string.Empty;
        if (Validadores.TryGetValue(clave, out var validar) && !validar(valor))
        {
            return BadRequest(new { message = $"Valor inválido para \"{clave}\": \"{valor}\"" });
        }

        // Apertura y cierre no pueden ser iguales (se compara contra la otra clave ya persistida).
        if (clave is "hora_apertura" or "hora_cierre")
        {
            var otraClave = clave == "hora_apertura" ? "hora_cierre" : "hora_apertura";
            var otraHora = await _db.Configuracions.AsNoTracking()
                .Where(c => c.Clave == otraClave).Select(c => c.Valor).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(otraHora) && otraHora == valor)
            {
                return BadRequest(new { message = "Las horas de apertura y cierre no pueden ser iguales" });
            }
        }

        // Upsert por (clave, negocioId): el Global Query Filter scopea el SELECT al negocio del request.
        var config = await _db.Configuracions.FirstOrDefaultAsync(c => c.Clave == clave);
        var now = Now();
        if (config is null)
        {
            config = new Configuracion
            {
                Id = Guid.NewGuid().ToString(),
                Clave = clave,
                Valor = valor,
                Descripcion = request.Descripcion,
                NegocioId = negocioId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Configuracions.Add(config);
        }
        else
        {
            config.Valor = valor;
            if (request.Descripcion is not null) config.Descripcion = request.Descripcion;
            config.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        return Ok(new ConfigItemResponse(config.Id, config.Clave, config.Valor, config.Descripcion, config.CreatedAt, config.UpdatedAt));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<int> ParseDiasAtencion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new[] { 1, 2, 3, 4, 5, 6, 7 };
        var dias = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n is >= 1 and <= 7)
            .ToList();
        return dias.Count > 0 ? dias : new List<int> { 1, 2, 3, 4, 5, 6, 7 };
    }

    private static string RazonToString(RazonEstadoLocal razon) => razon switch
    {
        RazonEstadoLocal.Abierto => "abierto",
        RazonEstadoLocal.FueraDeHorario => "fuera_de_horario",
        RazonEstadoLocal.DiaNoLaboral => "dia_no_laboral",
        _ => "config_invalida",
    };

    private static bool EsHoraHhMm(string v)
    {
        if (!Regex.IsMatch(v, @"^\d{2}:\d{2}$")) return false;
        var partes = v.Split(':');
        return int.TryParse(partes[0], out var h) && int.TryParse(partes[1], out var m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59;
    }

    private static bool EsEnteroEnRango(string v, int min, int max) =>
        Regex.IsMatch(v, @"^\d+$") && int.TryParse(v, out var n) && n >= min && n <= max;

    private static bool EsFloatNoNegativo(string v) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0;

    private static bool EsDiasAtencion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        if (!Regex.IsMatch(v, @"^([1-7])(,[1-7])*$")) return false;
        var nums = v.Split(',').Select(int.Parse).ToList();
        return nums.Distinct().Count() == nums.Count;
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
