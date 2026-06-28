using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class Proveedor
{
    public string Id { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public string? Telefono { get; set; }

    public string? Email { get; set; }

    public string? Notas { get; set; }

    public bool Activo { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public virtual ICollection<Insumo> Insumos { get; set; } = new List<Insumo>();

    public virtual Negocio Negocio { get; set; } = null!;
}
