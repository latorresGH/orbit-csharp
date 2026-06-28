using System;
using System.Collections.Generic;

namespace OrbIT.Infrastructure.Models;

public partial class WebhookEvent
{
    public string Id { get; set; } = null!;

    public string ExternalId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
