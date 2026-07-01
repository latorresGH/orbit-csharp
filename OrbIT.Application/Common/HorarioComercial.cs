namespace OrbIT.Application.Common;

/// <summary>Por qué el local está abierto o cerrado.</summary>
public enum RazonEstadoLocal
{
    Abierto,
    FueraDeHorario,
    DiaNoLaboral,
    ConfigInvalida,
}

/// <summary>Próxima apertura (día 1=lunes..7=domingo + hora "HH:mm").</summary>
public sealed record ProximaApertura(int Dia, string Hora);

/// <summary>Resultado de evaluar si el local está abierto ahora.</summary>
public sealed record EstadoLocal(bool Abierto, RazonEstadoLocal Razon, ProximaApertura? ProximaApertura);

/// <summary>
/// Port autónomo del <c>calcularEstadoLocal</c> de NestJS (<c>common/helpers/horario.helper.ts</c>),
/// reusando <see cref="ArgentinaClock"/> para la hora local. Resuelve si el local está abierto según
/// horario de apertura/cierre (soporta turno nocturno que cruza medianoche), día comercial y días de
/// atención, y calcula la próxima apertura para los mensajes de error.
/// </summary>
public static class HorarioComercial
{
    /// <summary>Nombres de día (1=lunes..7=domingo) para los mensajes al cliente.</summary>
    public static readonly IReadOnlyDictionary<int, string> NombresDias = new Dictionary<int, string>
    {
        [1] = "lunes",
        [2] = "martes",
        [3] = "miércoles",
        [4] = "jueves",
        [5] = "viernes",
        [6] = "sábado",
        [7] = "domingo",
    };

    public static EstadoLocal CalcularEstado(string horaApertura, string horaCierre, IReadOnlyList<int> diasAtencion)
    {
        var invalido = new EstadoLocal(false, RazonEstadoLocal.ConfigInvalida, null);

        if (diasAtencion.Count == 0) return invalido;
        if (horaApertura == horaCierre) return invalido;

        var aperMin = ParseHoraMinutos(horaApertura);
        var cierreMin = ParseHoraMinutos(horaCierre);
        if (aperMin is null || cierreMin is null) return invalido;

        var ahora = ArgentinaClock.Now();
        var diaSemana = ArgentinaClock.DiaSemana(ahora);
        var horaEnMinutos = ahora.Hour * 60 + ahora.Minute;

        // Día comercial: en un turno nocturno la madrugada pertenece a la jornada del día anterior.
        var diaComercial = CalcularDiaComercial(diaSemana, horaEnMinutos, aperMin.Value, cierreMin.Value);

        if (!diasAtencion.Contains(diaComercial))
        {
            var proxima = CalcularProximaApertura(diaSemana, horaEnMinutos, horaApertura, aperMin.Value, diasAtencion);
            return new EstadoLocal(false, RazonEstadoLocal.DiaNoLaboral, proxima);
        }

        bool abierto;
        if (aperMin <= cierreMin)
        {
            abierto = horaEnMinutos >= aperMin && horaEnMinutos < cierreMin;
        }
        else
        {
            // Turno nocturno: la apertura cruza la medianoche.
            abierto = horaEnMinutos >= aperMin || horaEnMinutos < cierreMin;
        }

        if (abierto)
        {
            return new EstadoLocal(true, RazonEstadoLocal.Abierto, null);
        }

        var proximaApertura = CalcularProximaApertura(diaSemana, horaEnMinutos, horaApertura, aperMin.Value, diasAtencion);
        return new EstadoLocal(false, RazonEstadoLocal.FueraDeHorario, proximaApertura);
    }

    private static int CalcularDiaComercial(int diaActual, int horaActualMin, int horaAperturaMin, int horaCierreMin)
    {
        if (horaAperturaMin > horaCierreMin && horaActualMin < horaCierreMin)
        {
            return diaActual == 1 ? 7 : diaActual - 1;
        }
        return diaActual;
    }

    private static ProximaApertura? CalcularProximaApertura(
        int diaActual, int horaActualMin, string horaApertura, int horaAperturaMin, IReadOnlyList<int> diasAtencion)
    {
        if (diasAtencion.Count == 0) return null;

        // Hoy (calendario) atendemos y todavía no abrimos.
        if (diasAtencion.Contains(diaActual) && horaActualMin < horaAperturaMin)
        {
            return new ProximaApertura(diaActual, horaApertura);
        }

        // Próximo día de atención (1..7 días hacia adelante).
        for (var offset = 1; offset <= 7; offset++)
        {
            var siguiente = ((diaActual - 1 + offset) % 7) + 1; // 1..7
            if (diasAtencion.Contains(siguiente))
            {
                return new ProximaApertura(siguiente, horaApertura);
            }
        }

        return null;
    }

    private static int? ParseHoraMinutos(string hora)
    {
        var partes = hora.Split(':');
        if (partes.Length != 2) return null;
        if (!int.TryParse(partes[0], out var h) || !int.TryParse(partes[1], out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return h * 60 + m;
    }
}
