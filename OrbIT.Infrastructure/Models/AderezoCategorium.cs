using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class AderezoCategorium
{
    public string Id { get; set; } = null!;

    public string AderezoId { get; set; } = null!;

    public string CategoriaId { get; set; } = null!;

    public string NegocioId { get; set; } = null!;

    public virtual Aderezo Aderezo { get; set; } = null!;

    public virtual Categorium Categoria { get; set; } = null!;

    public virtual Negocio Negocio { get; set; } = null!;
}
