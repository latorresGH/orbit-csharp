using OrbIT.Infrastructure.Models;

namespace OrbIT.IntegrationTests;

/// <summary>
/// Siembra los dos planes canónicos (Básico / Pro) en la base de test. La tabla <c>Plan</c> es global y no se
/// crea con <c>HasData</c>, así que cada fixture que ejerza endpoints guardados por <see cref="Plan"/> debe
/// sembrarla: sin fila <c>basic</c>, el <c>PlanGuard</c> cae a null (fail-closed) y todo devuelve 403.
///
/// Los valores replican el seed real de <c>orbit_csharp</c>: Básico 30 productos / 5 usuarios, sin
/// mesas/imágenes/reportes/ofertas; Pro ilimitado (-1) / 12 usuarios, todas las features on.
/// </summary>
public static class PlanSeed
{
    public const string BasicId = "plan-basic";
    public const string ProId = "plan-pro";

    public static void Seed(OrbitDbContext db, DateTime now)
    {
        db.Plans.AddRange(
            new Plan
            {
                Id = BasicId,
                Nombre = "Orb.IT Básico",
                Slug = "basic",
                PrecioMensual = 0,
                LimiteProductos = 30,
                LimiteUsuarios = 5,
                TieneMesas = false,
                TieneImagenes = false,
                TieneSignalR = false,
                TieneReportes = false,
                TieneToppingGrupos = true,
                TieneOfertas = false,
                TieneInsumos = true,
                Activo = true,
                CreatedAt = now,
            },
            new Plan
            {
                Id = ProId,
                Nombre = "Orb.IT Pro",
                Slug = "pro",
                PrecioMensual = 0,
                LimiteProductos = -1,
                LimiteUsuarios = 12,
                TieneMesas = true,
                TieneImagenes = true,
                TieneSignalR = true,
                TieneReportes = true,
                TieneToppingGrupos = true,
                TieneOfertas = true,
                TieneInsumos = true,
                Activo = true,
                CreatedAt = now,
            });
    }
}
