using Microsoft.EntityFrameworkCore;
using OrbIT.Application.Common;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.CodigosDescuento;

/// <summary>
/// Implementación de <see cref="ICodigosDescuentoService"/> sobre el <see cref="OrbitDbContext"/>.
/// Réplica del <c>CodigosDescuentoService.validar</c> de NestJS: normaliza el código (UPPER+trim), lo
/// busca por <c>(codigo, negocioId)</c> y devuelve un resultado con el motivo de rechazo (sin lanzar).
/// </summary>
public sealed class CodigosDescuentoService : ICodigosDescuentoService
{
    private readonly OrbitDbContext _db;

    public CodigosDescuentoService(OrbitDbContext db) => _db = db;

    public async Task<ValidacionCodigo> ValidarAsync(
        string codigo,
        string negocioId,
        string? productoId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizado = codigo.ToUpperInvariant().Trim();
        var ahora = ArgentinaClock.Now();

        var entidad = await _db.CodigoDescuentos.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Codigo == normalizado && c.NegocioId == negocioId, cancellationToken);

        if (entidad is null) return new ValidacionCodigo(false, "Código no válido");
        if (!entidad.Activo) return new ValidacionCodigo(false, "El código no está activo");
        if (ahora < entidad.FechaInicio) return new ValidacionCodigo(false, "El código todavía no está vigente");
        if (ahora > entidad.FechaFin) return new ValidacionCodigo(false, "El código ya venció");
        if (entidad.UsosMaximos is { } max && entidad.UsosActuales >= max)
        {
            return new ValidacionCodigo(false, "El código ya alcanzó el máximo de usos");
        }
        if (entidad.ProductoId is not null && productoId is not null && entidad.ProductoId != productoId)
        {
            return new ValidacionCodigo(false, "El código no aplica a este producto");
        }

        return new ValidacionCodigo(
            Valido: true,
            Codigo: new CodigoInfo(
                entidad.Id, entidad.Codigo, entidad.Descripcion, entidad.TipoDescuento,
                entidad.Valor, entidad.ProductoId, entidad.UsosMaximos),
            Descuento: new DescuentoInfo(entidad.TipoDescuento, entidad.Valor));
    }

    public Task IncrementarUsoAsync(string codigoId, CancellationToken cancellationToken = default) =>
        _db.CodigoDescuentos
            .Where(c => c.Id == codigoId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsosActuales, c => c.UsosActuales + 1), cancellationToken);
}
