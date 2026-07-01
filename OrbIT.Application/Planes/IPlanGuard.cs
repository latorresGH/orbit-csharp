namespace OrbIT.Application.Planes;

/// <summary>
/// Features booleanas de un <c>Plan</c>. Cada valor mapea 1:1 a una columna <c>tieneXxx</c> de la tabla Plan.
/// Se usa con <see cref="IPlanGuard.VerificarFeatureAsync"/> para preguntar si el plan del negocio la habilita.
/// </summary>
public enum PlanFeature
{
    Mesas,
    Imagenes,
    SignalR,
    Reportes,
    ToppingGrupos,
    Ofertas,
    Insumos,
}

/// <summary>
/// Resultado de una verificación de límite: si se puede seguir agregando (<paramref name="Permitido"/>),
/// cuántos hay actualmente y cuál es el tope del plan. <paramref name="Limite"/> negativo = ilimitado.
/// </summary>
/// <param name="Permitido">Hay lugar para al menos uno más (o el límite es ilimitado).</param>
/// <param name="Actual">Cantidad actual de recursos activos del negocio.</param>
/// <param name="Limite">Tope del plan; valor negativo (ej. -1) significa ilimitado.</param>
public readonly record struct LimiteResult(bool Permitido, int Actual, int Limite)
{
    /// <summary>El límite es ilimitado (plan Pro usa -1).</summary>
    public bool Ilimitado => Limite < 0;
}

/// <summary>
/// Guard de plan: valida límites y features del <c>Plan</c> contratado por un negocio antes de permitir una
/// acción (crear producto, dar de alta un usuario, usar mesas/imágenes/etc.). Port de las verificaciones de
/// plan del NestJS. El plan se resuelve por el <c>planId</c> del negocio; si no tiene plan asignado se cae al
/// plan <c>basic</c> (fail-closed hacia el plan más restrictivo).
///
/// NOTA: por ahora sólo se implementa y registra en DI. No está enganchado a ningún controller todavía
/// (se aplicará a Productos/Usuarios/Mesas/etc. en una iteración posterior).
/// </summary>
public interface IPlanGuard
{
    /// <summary>
    /// ¿Puede el negocio crear un producto más? Cuenta productos activos y los compara contra
    /// <c>plan.limiteProductos</c> (-1 = ilimitado). No lanza: devuelve el <see cref="LimiteResult"/>.
    /// </summary>
    Task<LimiteResult> VerificarLimiteProductosAsync(string negocioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿Puede el negocio dar de alta un usuario más? Cuenta usuarios activos y los compara contra
    /// <c>plan.limiteUsuarios</c> (-1 = ilimitado). No lanza: devuelve el <see cref="LimiteResult"/>.
    /// </summary>
    Task<LimiteResult> VerificarLimiteUsuariosAsync(string negocioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿El plan del negocio habilita la <paramref name="feature"/>? Devuelve el bool <c>tieneXxx</c> del plan;
    /// sin plan resuelto → <c>false</c> (fail-closed).
    /// </summary>
    Task<bool> VerificarFeatureAsync(string negocioId, PlanFeature feature, CancellationToken cancellationToken = default);
}
