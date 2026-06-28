using System;
using System.Collections.Generic;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

public partial class Turno
{
    public string Id { get; set; } = null!;

    public TipoTurno Tipo { get; set; }

    public string UserId { get; set; } = null!;

    public DateTime HoraInicio { get; set; }

    public DateTime? HoraFin { get; set; }

    public double CajaAperturaMonto { get; set; }

    public double? CajaCierreMonto { get; set; }

    public double VentasTotales { get; set; }

    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; }

    public string NegocioId { get; set; } = null!;

    public double? MontoEsperado { get; set; }

    public virtual Negocio Negocio { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
