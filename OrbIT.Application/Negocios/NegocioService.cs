using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Application.Auth;
using OrbIT.Application.Email;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Application.Negocios;

/// <summary>
/// Implementación de <see cref="INegocioService"/>. Réplica funcional del onboarding/lifecycle de NestJS
/// (<c>negocio.service.ts</c>). Las queries de negocio/usuario van con <c>IgnoreQueryFilters</c> porque estos
/// flujos corren fuera de un tenant resuelto (registro público, verificación por slug, SUPERADMIN).
/// </summary>
public sealed class NegocioService : INegocioService
{
    private const string UniqueViolation = "23505";
    private const int TrialDias = 7;
    private const int CodigoTtlMinutos = 15;
    private const int GraciaDias = 16;

    /// <summary>15 claves de configuración que se siembran al crear un negocio (paridad con NestJS).</summary>
    private static readonly (string Clave, string Valor, string Descripcion)[] ConfigDefaults =
    {
        ("alias_transferencia", "alias.negocio", "Alias para recibir transferencias"),
        ("whatsapp_numero", "", "Número de WhatsApp para contacto (con código de país)"),
        ("stock_min_unidad", "10", "Stock mínimo para insumos medidos en unidades"),
        ("stock_min_gramo", "500", "Stock mínimo para insumos medidos en gramos"),
        ("stock_min_kilogramo", "1", "Stock mínimo para insumos medidos en kilogramos"),
        ("stock_min_mililitro", "500", "Stock mínimo para insumos medidos en mililitros"),
        ("stock_min_litro", "1", "Stock mínimo para insumos medidos en litros"),
        ("stock_min_pote", "5", "Stock mínimo para insumos medidos en potes"),
        ("stock_min_sobre", "10", "Stock mínimo para insumos medidos en sobres"),
        ("stock_min_feta", "10", "Stock mínimo para insumos medidos en fetas"),
        ("delivery_precio_base", "3000", "Precio base de delivery"),
        ("hora_apertura", "21:00", "Hora de apertura del local (formato HH:MM)"),
        ("hora_cierre", "23:30", "Hora de cierre del local (formato HH:MM)"),
        ("costo_envio_base", "3000", "Costo base de envío (mostrado como estimado)"),
        ("dias_atencion", "1,2,3,4,5,6,7", "Días de atención (1=Lunes, 7=Domingo, separados por coma)"),
    };

    private readonly OrbitDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IEmailService _email;

    public NegocioService(OrbitDbContext db, IPasswordHasher hasher, IEmailService email)
    {
        _db = db;
        _hasher = hasher;
        _email = email;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Registro público
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<RegistroResult> RegistrarNuevoNegocioAsync(RegistroNegocioInput input, CancellationToken ct = default)
    {
        var slug = input.Slug;
        var emailLower = input.Email.Trim().ToLowerInvariant();

        if (await _db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Slug == slug, ct))
        {
            throw NegocioException.Conflict("Este slug ya está en uso, elegí otro");
        }
        // Paridad con NestJS: chequea el email bajo ese slug (para un slug nuevo siempre es vacío).
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == emailLower && u.Negocio!.Slug == slug, ct))
        {
            throw NegocioException.Conflict("Este email ya está registrado");
        }

        var codigo = GenerarCodigo();
        var now = Now();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var negocioId = Guid.NewGuid().ToString();
        _db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = input.NombreNegocio,
            Slug = slug,
            Plan = "trial",
            Activo = true,
            TrialExpira = now.AddDays(TrialDias),
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.Users.Add(new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = emailLower,
            Password = _hasher.Hash(input.Password),
            Nombre = input.NombreAdmin.Trim(),
            Role = Role.ADMIN,
            NegocioId = negocioId,
            Activo = true,
            EmailVerificado = false,
            CodigoVerificacion = codigo,
            CodigoExpira = now.AddMinutes(CodigoTtlMinutos),
            CreatedAt = now,
        });

        SembrarConfigYDemora(negocioId, now);

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw NegocioException.Conflict("Este slug ya está en uso, elegí otro");
        }

        await _email.EnviarCodigoVerificacionAsync(emailLower, codigo, input.NombreNegocio, ct);

        return new RegistroResult(emailLower, slug);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Alta manual (SUPERADMIN)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<NegocioCreadoResult> CrearConAdminAsync(CrearConAdminInput input, CancellationToken ct = default)
    {
        if (await _db.Negocios.IgnoreQueryFilters().AnyAsync(n => n.Slug == input.Slug, ct))
        {
            throw NegocioException.Conflict("El slug ya está en uso");
        }

        var now = Now();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var negocioId = Guid.NewGuid().ToString();
        var trialExpira = now.AddDays(TrialDias);
        _db.Negocios.Add(new Negocio
        {
            Id = negocioId,
            Nombre = input.Nombre,
            Slug = input.Slug,
            Plan = "trial",
            Activo = true,
            TrialExpira = trialExpira,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.Users.Add(new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = input.AdminEmail.Trim().ToLowerInvariant(),
            Password = _hasher.Hash(input.AdminPassword),
            Nombre = input.AdminNombre.Trim(),
            Role = Role.ADMIN,
            NegocioId = negocioId,
            Activo = true,
            EmailVerificado = true, // alta manual: sin flujo de verificación por email
            CreatedAt = now,
        });

        SembrarConfigYDemora(negocioId, now);

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw NegocioException.Conflict("El slug ya está en uso");
        }

        return new NegocioCreadoResult(negocioId, input.Nombre, input.Slug, "trial", trialExpira);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Verificación de email
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<VerificacionResult> VerificarEmailAsync(VerificacionInput input, CancellationToken ct = default)
    {
        var emailLower = input.Email.Trim().ToLowerInvariant();
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Slug == input.NegocioSlug, ct)
            ?? throw NegocioException.NotFound("Negocio no encontrado");

        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == emailLower && u.NegocioId == negocio.Id, ct)
            ?? throw NegocioException.NotFound("Usuario no encontrado");

        // Ya verificado: idempotente, se emite sesión igual (paridad con NestJS).
        if (user.EmailVerificado == true)
        {
            return ResultadoDe(user);
        }

        var now = Now();
        if (user.BloqueadoHasta is { } bloqueado && bloqueado > now)
        {
            var minutos = (int)Math.Ceiling((bloqueado - now).TotalMinutes);
            throw NegocioException.BadRequest($"Demasiados intentos. Intentá en {minutos} {(minutos == 1 ? "minuto" : "minutos")}.");
        }

        if (user.CodigoExpira is not { } expira || expira < now)
        {
            throw NegocioException.BadRequest("El código venció. Solicitá uno nuevo.");
        }

        if (user.CodigoVerificacion is null || user.CodigoVerificacion != input.Codigo)
        {
            var intentos = user.IntentosVerificacion + 1;
            user.IntentosVerificacion = intentos;
            user.BloqueadoHasta = intentos switch
            {
                >= 9 => now.AddHours(24),
                >= 6 => now.AddMinutes(30),
                >= 3 => now.AddMinutes(5),
                _ => null,
            };
            await _db.SaveChangesAsync(ct);
            throw NegocioException.BadRequest("Código incorrecto");
        }

        user.EmailVerificado = true;
        user.CodigoVerificacion = null;
        user.CodigoExpira = null;
        user.IntentosVerificacion = 0;
        user.BloqueadoHasta = null;
        await _db.SaveChangesAsync(ct);

        return ResultadoDe(user);
    }

    public async Task ReenviarCodigoAsync(string email, string negocioSlug, CancellationToken ct = default)
    {
        var emailLower = email.Trim().ToLowerInvariant();
        var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Slug == negocioSlug, ct)
            ?? throw NegocioException.NotFound("Negocio no encontrado");

        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == emailLower && u.NegocioId == negocio.Id, ct)
            ?? throw NegocioException.NotFound("Usuario no encontrado");

        if (user.EmailVerificado == true)
        {
            throw NegocioException.BadRequest("El email ya está verificado");
        }

        var now = Now();
        // Anti-spam: si el código todavía vence en más de 14 min, se generó hace menos de 1 min.
        if (user.CodigoExpira is { } expira && (expira - now).TotalMinutes > CodigoTtlMinutos - 1)
        {
            throw NegocioException.BadRequest("Esperá al menos 1 minuto antes de reenviar");
        }

        var codigo = GenerarCodigo();
        user.CodigoVerificacion = codigo;
        user.CodigoExpira = now.AddMinutes(CodigoTtlMinutos);
        await _db.SaveChangesAsync(ct);

        await _email.EnviarCodigoVerificacionAsync(user.Email, codigo, negocio.Nombre, ct);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Purga de cuentas cerradas
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Borra físicamente los negocios con cuenta cerrada hace más de 16 días. Réplica del cron de NestJS pero
    /// con <c>ExecuteDelete</c> (bulk server-side, sin traer entidades). Se borra en orden de dependencias
    /// (los hijos intra-agregado como ExtraPrecio/OfertaProducto caen por cascada de su padre); se añaden
    /// explícitamente CodigoDescuento/AuditLog/RefreshToken (FK negocioId Restrict, fuera del árbol borrado)
    /// para no depender de cascadas. Cada negocio va en su propia transacción.
    /// </summary>
    public async Task<int> LimpiarCuentasCerradasAsync(CancellationToken ct = default)
    {
        var limite = Now().AddDays(-GraciaDias);
        var ids = await _db.Negocios.IgnoreQueryFilters()
            .Where(n => n.CuentaCerradaAt != null && n.CuentaCerradaAt <= limite && !n.Activo)
            .Select(n => n.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Romper la FK circular Mesa ↔ Pedido antes de borrar pedidos.
            await _db.Mesas.IgnoreQueryFilters().Where(m => m.NegocioId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.PedidoActivoId, (string?)null), ct);

            await Borrar(_db.PizzaMediaMedia, id, ct);
            await Borrar(_db.PedidoOferta, id, ct);
            await Borrar(_db.CajaMovimientos, id, ct);
            await Borrar(_db.StockMovimientos, id, ct);
            await Borrar(_db.PedidoDetalles, id, ct);
            await Borrar(_db.CodigoDescuentos, id, ct);
            await Borrar(_db.Pedidos, id, ct);
            await Borrar(_db.Turnos, id, ct);
            await Borrar(_db.GastoOperativos, id, ct);
            await Borrar(_db.Barrios, id, ct);
            await Borrar(_db.Oferta, id, ct);           // cascadea OfertaProducto, GrupoCombo → GrupoOpcion
            await Borrar(_db.ProductoReceta, id, ct);
            await Borrar(_db.Extras, id, ct);           // cascadea ExtraPrecio, ExtraCategoria, ExtraConsumo
            await Borrar(_db.ToppingGrupos, id, ct);
            await Borrar(_db.Aderezos, id, ct);         // cascadea AderezoPrecio, AderezoCategoria, AderezoConsumo
            await Borrar(_db.Productos, id, ct);
            await Borrar(_db.Categoria, id, ct);
            await Borrar(_db.Insumos, id, ct);
            await Borrar(_db.Proveedors, id, ct);
            await Borrar(_db.DemoraConfigs, id, ct);
            await Borrar(_db.Configuracions, id, ct);
            await Borrar(_db.Mesas, id, ct);
            await Borrar(_db.Clientes, id, ct);
            await Borrar(_db.AuditLogs, id, ct);
            await Borrar(_db.RefreshTokens, id, ct);
            await Borrar(_db.Users, id, ct);
            await _db.Negocios.IgnoreQueryFilters().Where(n => n.Id == id).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        }

        return ids.Count;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static Task Borrar<T>(DbSet<T> set, string negocioId, CancellationToken ct) where T : class =>
        set.IgnoreQueryFilters().Where(BuildNegocioPredicate<T>(negocioId)).ExecuteDeleteAsync(ct);

    /// <summary>Predicado <c>e =&gt; e.NegocioId == id</c> construido por reflexión (todas las entidades hijas lo tienen).</summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> BuildNegocioPredicate<T>(string negocioId)
    {
        var param = System.Linq.Expressions.Expression.Parameter(typeof(T), "e");
        var prop = System.Linq.Expressions.Expression.Property(param, "NegocioId");
        var body = System.Linq.Expressions.Expression.Equal(prop, System.Linq.Expressions.Expression.Constant(negocioId));
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, param);
    }

    private void SembrarConfigYDemora(string negocioId, DateTime now)
    {
        foreach (var (clave, valor, descripcion) in ConfigDefaults)
        {
            _db.Configuracions.Add(new Configuracion
            {
                Id = Guid.NewGuid().ToString(),
                Clave = clave,
                Valor = valor,
                Descripcion = descripcion,
                NegocioId = negocioId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _db.DemoraConfigs.Add(new DemoraConfig
        {
            Id = Guid.NewGuid().ToString(),
            NegocioId = negocioId,
            Modo = "AUTO",
            ValorManual = 0,
            Activo = false,
            UpdatedAt = now,
        });
    }

    private static VerificacionResult ResultadoDe(User user) =>
        new(user.Id, user.Role, user.NegocioId, new UsuarioInfo(user.Id, user.Email, user.Nombre, user.Role));

    private static string GenerarCodigo() => Random.Shared.Next(100000, 1000000).ToString();

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
