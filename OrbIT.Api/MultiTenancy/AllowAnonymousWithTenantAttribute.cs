using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OrbIT.Api.MultiTenancy;

/// <summary>
/// Marca una acción como pública con resolución de tenant — el equivalente idiomático en ASP.NET Core
/// del combo <c>@Public() + @UseGuards(OptionalJwtGuard)</c> de NestJS. Hace dos cosas a la vez:
/// <list type="bullet">
///   <item>implementa <see cref="IAllowAnonymous"/>, que desactiva el <c>[Authorize]</c> a nivel
///   controller para esta acción (la vuelve accesible sin JWT);</item>
///   <item>implementa <see cref="IFilterFactory"/> para enganchar el <see cref="ResolveTenantBySlugFilter"/>,
///   que deja resuelto el negocio activo por claim (si hay sesión) o por <c>?negocio=slug</c> (si es
///   anónima).</item>
/// </list>
/// Convención del proyecto para CUALQUIER endpoint público futuro (menú público de Productos, Extras,
/// Ofertas, Config, Barrios, Categorías): basta marcar la acción con este atributo y dejar que el query
/// filter multi-tenant haga el resto. Ver CLAUDE.md, sección "Endpoints públicos".
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowAnonymousWithTenantAttribute : Attribute, IAllowAnonymous, IFilterFactory
{
    // El filtro usa servicios scoped (DbContext, TenantResolutionContext), así que no es reusable:
    // se resuelve del contenedor en cada request.
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<ResolveTenantBySlugFilter>();
}
