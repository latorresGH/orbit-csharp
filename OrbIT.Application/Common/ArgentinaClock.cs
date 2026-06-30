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
