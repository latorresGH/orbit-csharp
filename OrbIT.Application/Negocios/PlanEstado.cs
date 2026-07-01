namespace OrbIT.Application.Negocios;

/// <summary>
/// Cálculo del estado del plan de un negocio (port del <c>obtenerEstadoPlan</c> de NestJS). Devuelve los
/// mismos strings que el original para no romper el contrato con el frontend:
/// <c>trial_activo</c> · <c>trial_vencido</c> · <c>activo</c> · <c>inactivo</c>.
/// </summary>
public static class PlanEstado
{
    public const string TrialActivo = "trial_activo";
    public const string TrialVencido = "trial_vencido";
    public const string Activo = "activo";
    public const string Inactivo = "inactivo";

    public static string Calcular(bool activo, string plan, DateTime? trialExpira)
    {
        if (!activo) return Inactivo;
        if (plan == "activo") return Activo;
        if (plan == "trial")
        {
            return trialExpira is { } exp && exp > DateTime.UtcNow ? TrialActivo : TrialVencido;
        }
        return Inactivo;
    }

    /// <summary>Días restantes de trial (hacia arriba), o null si no está en trial activo.</summary>
    public static int? DiasRestantesTrial(string estadoPlan, DateTime? trialExpira)
    {
        if (estadoPlan != TrialActivo || trialExpira is not { } exp) return null;
        var ms = (exp - DateTime.UtcNow).TotalDays;
        return (int)Math.Ceiling(ms);
    }
}
