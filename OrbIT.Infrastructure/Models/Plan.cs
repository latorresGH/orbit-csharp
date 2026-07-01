using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Plan de suscripción del sistema. Tabla GLOBAL (no por tenant): define los límites y features que un
/// <see cref="Negocio"/> tiene contratados. Agregada a mano post-scaffold (misma vía que otros ajustes),
/// ya que la migración se corrió por SQL directo contra orbit_csharp. Si se vuelve a scaffoldear la base,
/// reaplicar esta entidad, su <c>DbSet</c> y la relación con <see cref="Negocio"/> (sin Global Query Filter).
/// </summary>
public partial class Plan
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public double PrecioMensual { get; set; }

    public string? MpPlanId { get; set; }

    public int LimiteProductos { get; set; }

    public int LimiteUsuarios { get; set; }

    public bool TieneMesas { get; set; }

    public bool TieneImagenes { get; set; }

    public bool TieneSignalR { get; set; }

    public bool TieneReportes { get; set; }

    public bool TieneToppingGrupos { get; set; }

    public bool TieneOfertas { get; set; }

    public bool TieneInsumos { get; set; }

    public bool Activo { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Negocio> Negocios { get; set; } = new List<Negocio>();
}
