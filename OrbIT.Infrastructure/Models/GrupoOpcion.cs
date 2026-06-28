using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class GrupoOpcion
{
    public string Id { get; set; } = null!;

    public string GrupoComboId { get; set; } = null!;

    public string ProductoId { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public virtual GrupoCombo GrupoCombo { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual Producto Producto { get; set; } = null!;
}
