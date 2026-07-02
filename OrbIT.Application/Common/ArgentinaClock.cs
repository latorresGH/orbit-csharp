using System.Globalization;

namespace OrbIT.Application.Common;

/// <summary>
/// Reloj local de Argentina (<c>America/Argentina/Buenos_Aires</c>), centralizado y reutilizable.
/// Replica el <c>toLocaleString('...', { timeZone: 'America/Argentina/Buenos_Aires' })</c> que usa el
/// NestJS para resolver qué ofertas están vigentes según día/hora local.
///
/// Resuelve la zona por ID IANA (funciona en .NET 6+ por ICU, en Windows y Linux); si el runtime no la
/// tiene, cae al ID de Windows y por último a un offset fijo -03:00 (Argentina no tiene DST hoy).
/// </summary>
public static class ArgentinaClock
{
    private static readonly TimeZoneInfo Tz = ResolveTimeZone();

    /// <summary>Hora actual de Argentina como <see cref="DateTime"/> con <c>Kind=Unspecified</c> (hora de pared).</summary>
    public static DateTime Now() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz).DateTime;

    /// <summary>
    /// Día de la semana 1..7 con <b>lunes=1 y domingo=7</b> (convención del NestJS: <c>getDay()===0 ? 7 : getDay()</c>).
    /// </summary>
    public static int DiaSemana(DateTime local) =>
        local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;

    /// <summary>Hora local en formato <c>"HH:mm"</c> (para comparar contra <c>horaInicio/horaFin</c> de la oferta).</summary>
    public static string HoraHhMm(DateTime local) => local.ToString("HH:mm");

    /// <summary>
    /// Convierte un instante UTC (así se guarda <c>createdAt</c> en la DB: <c>DateTime.UtcNow</c> con
    /// <c>Kind=Unspecified</c>) a hora de pared de Argentina. Equivale al
    /// <c>toLocaleString('...', { timeZone: 'America/Argentina/Buenos_Aires' })</c> del NestJS.
    /// </summary>
    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    /// <summary>
    /// Convierte una hora de pared de Argentina a instante UTC (<c>Kind=Unspecified</c>, comparable contra
    /// <c>createdAt</c>). Reemplaza el truco de NestJS de concatenar <c>+ 'T00:00:00.000-03:00'</c> para
    /// armar los límites <c>desde</c>/<c>hasta</c> de los filtros de fecha.
    /// </summary>
    public static DateTime ToUtc(DateTime local) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Tz), DateTimeKind.Unspecified);

    /// <summary>
    /// Límite inferior de un filtro por fecha: inicio del día AR (00:00 -03:00) del día indicado por
    /// <paramref name="value"/> (cualquier fecha ISO parseable), como instante UTC (<c>Kind=Unspecified</c>)
    /// comparable contra columnas <c>createdAt</c>/<c>fecha</c>. Devuelve <c>null</c> si <paramref name="value"/>
    /// es vacío o no parsea. Centraliza el helper que estaba duplicado en los controllers de reporting.
    /// </summary>
    public static DateTime? DesdeArUtc(string? value) =>
        TryParseDate(value, out var d) ? ToUtc(d.Date) : null;

    /// <summary>
    /// Límite superior de un filtro por fecha: fin del día AR (23:59:59.999… -03:00) del día indicado por
    /// <paramref name="value"/>, como instante UTC comparable. Devuelve <c>null</c> si no parsea.
    /// </summary>
    public static DateTime? HastaArUtc(string? value) =>
        TryParseDate(value, out var d) ? ToUtc(d.Date.AddDays(1).AddTicks(-1)) : null;

    private static bool TryParseDate(string? value, out DateTime fecha)
    {
        fecha = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { "America/Argentina/Buenos_Aires", "Argentina Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Probar el siguiente id.
            }
            catch (InvalidTimeZoneException)
            {
                // Probar el siguiente id.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("ART", TimeSpan.FromHours(-3), "Argentina Time", "ART");
    }
}
