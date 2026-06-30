using System.Text.Json;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Audit;

/// <summary>
/// Implementación de <see cref="IAuditLogService"/> sobre el <see cref="OrbitDbContext"/>.
/// Se registra como scoped (mismo scope que el DbContext del request), así comparte la unidad de
/// trabajo del controller que lo invoca.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private readonly OrbitDbContext _db;

    public AuditLogService(OrbitDbContext db) => _db = db;

    public async Task RegistrarAsync(
        string accion,
        string entidad,
        string entidadId,
        object? detalle,
        string negocioId,
        string? usuarioId = null,
        string? ip = null,
        CancellationToken cancellationToken = default)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid().ToString(),
            Accion = accion,
            Entidad = entidad,
            EntidadId = entidadId,
            Detalle = detalle is null ? null : JsonSerializer.Serialize(detalle, (JsonSerializerOptions?)null),
            NegocioId = negocioId,
            UsuarioId = usuarioId,
            Ip = ip,
            // CreatedAt: lo genera la DB (DEFAULT CURRENT_TIMESTAMP vía sentinel DateTime.MinValue).
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
