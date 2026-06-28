using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class RefreshToken
{
    public string Id { get; set; } = null!;

    public string TokenHash { get; set; } = null!;

    public string UserId { get; set; } = null!;

    public string? NegocioId { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool Revocado { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
