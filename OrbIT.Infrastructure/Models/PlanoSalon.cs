using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Plano de salón de un negocio: el layout del editor drag &amp; drop. Es 1:1 con el negocio (índice único
/// sobre <c>negocioId</c>), así que cada negocio tiene a lo sumo un plano. Los elementos (mesas/paredes/barras)
/// se guardan como array jsonb en <see cref="Elementos"/>; el resto son las dimensiones del canvas.
///
/// No lleva Global Query Filter propio distinto al resto: se filtra por <c>NegocioId</c> contra el tenant
/// activo, igual que las demás entidades por-negocio (ver <c>OrbitDbContext.MultiTenant</c>).
/// </summary>
public partial class PlanoSalon
{
    public string Id { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public List<ElementoPlano> Elementos { get; set; } = new();

    public int CanvasWidth { get; set; }

    public int CanvasHeight { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;
}
