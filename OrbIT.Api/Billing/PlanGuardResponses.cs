using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace OrbIT.Api.Billing;

/// <summary>
/// Respuestas 403 estándar cuando el <c>IPlanGuard</c> bloquea una acción por plan. Centraliza el formato del
/// cuerpo (<c>{ message }</c>) y el status para que todos los controllers devuelvan el mismo contrato al front
/// (que muestra el mensaje y ofrece el upgrade). Los controllers no comparten clase base, así que esto vive
/// como helper estático reusable.
/// </summary>
internal static class PlanGuardResponses
{
    /// <summary>Feature no incluida en el plan (mesas, imágenes, reportes, ofertas): 403 con mensaje de upgrade.</summary>
    public static ObjectResult Feature() =>
        Forbidden("Tu plan Básico no incluye esta funcionalidad. Actualizá al plan Pro.");

    /// <summary>Límite de productos alcanzado: 403 informando el tope del plan.</summary>
    public static ObjectResult LimiteProductos(int limite) =>
        Forbidden($"Alcanzaste el límite de productos de tu plan ({limite}). Actualizá al plan Pro para agregar más.");

    /// <summary>Límite de usuarios alcanzado: 403 informando el tope del plan.</summary>
    public static ObjectResult LimiteUsuarios(int limite) =>
        Forbidden($"Alcanzaste el límite de usuarios de tu plan ({limite}). Actualizá al plan Pro para agregar más.");

    private static ObjectResult Forbidden(string message) =>
        new(new { message }) { StatusCode = StatusCodes.Status403Forbidden };
}
