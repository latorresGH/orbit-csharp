using System;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Token temporal de un solo uso (one-time token). Hoy sólo se usa para el OTT del flujo Google OAuth: el
/// callback genera uno (TTL 30s) y el <c>exchange</c> lo consume por única vez para emitir la sesión.
/// Resuelve el problema del store en memoria del NestJS (Map <c>ottStore</c>): al vivir en la DB sobrevive a
/// reinicios y es seguro con varias instancias. Tabla de SISTEMA, <b>sin Global Query Filter</b> (igual que
/// <see cref="RefreshToken"/>): las lecturas van con <c>IgnoreQueryFilters</c>/fuera de tenant.
///
/// <para>Agregada a mano post-scaffold (misma vía que <see cref="Plan"/>): la migración se corrió por SQL
/// directo contra orbit_csharp. Si se vuelve a scaffoldear la base, reaplicar esta entidad, su <c>DbSet</c> y
/// su configuración en <c>OnModelCreating</c> (sin Global Query Filter).</para>
/// </summary>
public partial class TempToken
{
    public string Id { get; set; } = null!;

    /// <summary>Hash SHA-256 (hex) del token opaco. El valor crudo sólo viaja al cliente, nunca se persiste.</summary>
    public string TokenHash { get; set; } = null!;

    public string UserId { get; set; } = null!;

    public string? NegocioId { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Marca de consumo: el <c>exchange</c> filtra por <c>usada = false</c> y la pone en true (single-use).</summary>
    public bool Usada { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
