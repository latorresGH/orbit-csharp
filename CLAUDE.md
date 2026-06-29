# Orb.IT — Proyecto C# / ASP.NET Core

## Contexto del proyecto

Este proyecto reconstruye desde cero, en C# / ASP.NET Core + Entity Framework Core, el backend de Orb.IT que hoy corre en producción en NestJS + Prisma. La estrategia es: construir todo en paralelo sin tocar el sistema NestJS en producción, y migrar el tráfico real de un solo movimiento cuando esté completo y validado.

El sistema NestJS es la referencia funcional (qué debe hacer cada endpoint, qué reglas de negocio aplican), pero **no es necesariamente la referencia de cómo implementarlo**. Mientras se construye cada módulo nuevo, aplicar las siguientes reglas:

## Regla fija: optimizar, no solo calcar

Para cada controller/service nuevo que se implemente:

1. **Replicar el comportamiento funcional exacto** de NestJS (las reglas de negocio, las validaciones, los códigos de error) — esto no es negociable, el sistema tiene que hacer lo mismo que en producción.

2. **Pero NO calcar la implementación 1:1 si EF Core / C# ofrece una forma mejor.** Antes de escribir el código, evaluar explícitamente:
   - ¿Esta query se puede hacer en una sola consulta a la DB en vez de varias (N+1)?
   - ¿Conviene paginación server-side en este endpoint, aunque NestJS no la tuviera? (especialmente en listados que pueden crecer: pedidos, historial, productos, clientes)
   - ¿Hay alguna validación que en NestJS se hacía a mano y EF Core puede garantizar estructuralmente (como ya hicimos con los Global Query Filters de multi-tenant)?
   - ¿El índice de la base de datos está optimizado para los queries reales de este endpoint, o falta alguno?
   - ¿Conviene usar projection (Select a un DTO directo) en vez de traer la entidad completa y mapear después, para reducir el tamaño de los datos transferidos desde Postgres?

3. **Reportar las optimizaciones encontradas antes de implementar**, como una sección aparte del plan: "Diferencias respecto al NestJS original (mejoras)". El dueño del proyecto decide si las aplica o prefiere mantener paridad exacta por ahora.

4. **No optimizar prematuramente cosas que no son cuellos de botella reales** — el objetivo es código limpio y eficiente por diseño, no sobre-ingeniería. Si una query simple sobre una tabla chica no necesita paginación, no hace falta agregarla "por si acaso".

## Convenciones ya establecidas (no repetir la discusión)

- Controllers tradicionales (no Minimal API).
- Entity Framework Core con scaffold desde la base real (orbit_csharp), nunca a mano.
- Global Query Filters para multi-tenant (fail-closed: sin tenant resuelto, no se ve nada).
- Patrón de controller: DTOs en OrbIT.Api/Contracts/{Modulo}/, FirstOrDefaultAsync (nunca FindAsync, rompe el filtro de tenant), stamping manual de NegocioId en Create (temporal, hasta implementar override de SaveChanges), pre-chequeo de duplicados → 409 antes que reventar la unique constraint.
- Tests de integración por HTTP real (WebApplicationFactory) para cada controller, validando aislamiento multi-tenant end-to-end.
- appsettings.json (commiteado, placeholders) + appsettings.Development.json (gitignoreado, valores reales).
- BCrypt work factor 10, JWT con cookies HttpOnly + SameSite=Lax, refresh diferenciado por rol (7 días ADMIN/SUPERADMIN, 12h TRABAJADOR/DELIVERY).

## Pendiente conocido (no implementar todavía salvo que se pida explícitamente)

- Stamping automático de NegocioId vía override de SaveChanges (hoy es manual por controller).
- Wiring final de NpgsqlDataSource en producción real (hoy solo verificado en desarrollo).