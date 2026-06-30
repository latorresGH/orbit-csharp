namespace OrbIT.Application.Audit;

/// <summary>
/// Servicio de auditoría reutilizable: registra una acción en la tabla <c>AuditLog</c>.
/// Pensado para que cualquier controller lo inyecte y deje rastro de operaciones sensibles
/// (cambios de precio, bajas, ajustes manuales, etc.) de forma uniforme.
///
/// Réplica del <c>AuditLogService.registrar</c> de NestJS, con dos diferencias deliberadas:
/// (1) acá la escritura es <b>await</b> real (no fire-and-forget) para que el registro sea
/// transaccionalmente observable en tests y producción; (2) se acepta <see cref="usuarioId"/>
/// (NestJS no lo seteaba en el CAMBIO_PRECIO de Producto — ver decisión B, mejora de trazabilidad).
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Persiste un registro de auditoría. <paramref name="detalle"/> se serializa a JSON y se guarda
    /// en la columna <c>jsonb</c> <c>detalle</c>. El <c>createdAt</c> lo pone la base (DEFAULT now()).
    /// </summary>
    Task RegistrarAsync(
        string accion,
        string entidad,
        string entidadId,
        object? detalle,
        string negocioId,
        string? usuarioId = null,
        string? ip = null,
        CancellationToken cancellationToken = default);
}
