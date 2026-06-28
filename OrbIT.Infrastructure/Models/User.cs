using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class User
{
    public string Id { get; set; } = null!;

    public Role Role { get; set; }

    public string Email { get; set; } = null!;

    public string Password { get; set; } = null!;

    public string Nombre { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public bool Activo { get; set; }

    public string? NegocioId { get; set; }

    public DateTime? CodigoExpira { get; set; }

    public string? CodigoVerificacion { get; set; }

    public bool? EmailVerificado { get; set; }

    public DateTime? BloqueadoHasta { get; set; }

    public int IntentosVerificacion { get; set; }

    public DateTime? BloqueadoLoginHasta { get; set; }

    public int IntentosLogin { get; set; }

    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();

    public virtual Negocio? Negocio { get; set; }

    public virtual ICollection<Pedido> Pedidos { get; set; } = new List<Pedido>();

    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public virtual ICollection<Turno> Turnos { get; set; } = new List<Turno>();
}
