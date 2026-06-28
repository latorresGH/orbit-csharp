using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class AuditLog
{
    public string Id { get; set; } = null!;

    public string Accion { get; set; } = null!;

    public string? Entidad { get; set; }

    public string? EntidadId { get; set; }

    public string? UsuarioId { get; set; }

    public string? NegocioId { get; set; }

    public string? Detalle { get; set; }

    public string? Ip { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User? Usuario { get; set; }
}
