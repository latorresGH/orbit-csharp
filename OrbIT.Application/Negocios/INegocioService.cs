using OrbIT.Domain.Enums;

namespace OrbIT.Application.Negocios;

// ── Inputs ───────────────────────────────────────────────────────────────────

public sealed record RegistroNegocioInput(string NombreNegocio, string Slug, string NombreAdmin, string Email, string Password);

public sealed record CrearConAdminInput(string Nombre, string Slug, string AdminEmail, string AdminPassword, string AdminNombre, string? Plan);

public sealed record VerificacionInput(string Email, string NegocioSlug, string Codigo);

// ── Results ──────────────────────────────────────────────────────────────────

/// <summary>Resultado del registro público: el email queda pendiente de verificación (no se emite sesión).</summary>
public sealed record RegistroResult(string Email, string Slug);

public sealed record UsuarioInfo(string Id, string Email, string Nombre, Role Role);

/// <summary>
/// Resultado de una verificación exitosa: los datos necesarios para que el controller emita la sesión
/// (cookies) y devuelva el usuario. No cruza la entidad <c>User</c> a la capa Api.
/// </summary>
public sealed record VerificacionResult(string UserId, Role Role, string? NegocioId, UsuarioInfo User);

public sealed record NegocioCreadoResult(string Id, string Nombre, string Slug, string Plan, DateTime? TrialExpira);

// ── Excepción de dominio ─────────────────────────────────────────────────────

/// <summary>
/// Excepción de dominio del módulo Negocio. El controller la mapea a 400/404/409. Mismo patrón que
/// <c>PedidoException</c> / <c>CajaException</c>.
/// </summary>
public sealed class NegocioException : Exception
{
    public int StatusCode { get; }

    private NegocioException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public static NegocioException BadRequest(string message) => new(400, message);

    public static NegocioException NotFound(string message) => new(404, message);

    public static NegocioException Conflict(string message) => new(409, message);
}

/// <summary>
/// Operaciones transaccionales / pesadas del módulo Negocio: onboarding (registro público con verificación
/// de email por código, y alta manual de SUPERADMIN), verificación/reenvío de código, y la purga de cuentas
/// cerradas. El resto (perfil, estado, CRUD SUPERADMIN de un solo update) vive en el controller.
/// </summary>
public interface INegocioService
{
    /// <summary>
    /// Registro público: crea negocio + admin (no verificado) + configs por defecto + DemoraConfig en una
    /// transacción, con trial de 7 días, genera el código de verificación y lo "envía" (stub). No emite sesión.
    /// </summary>
    Task<RegistroResult> RegistrarNuevoNegocioAsync(RegistroNegocioInput input, CancellationToken cancellationToken = default);

    /// <summary>Alta manual de SUPERADMIN: como el registro pero el admin queda verificado y sin código.</summary>
    Task<NegocioCreadoResult> CrearConAdminAsync(CrearConAdminInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica el código de email. Aplica lockout progresivo ante intentos fallidos. Si el código es válido
    /// (o el email ya estaba verificado), marca verificado y devuelve los datos para emitir la sesión.
    /// </summary>
    Task<VerificacionResult> VerificarEmailAsync(VerificacionInput input, CancellationToken cancellationToken = default);

    /// <summary>Reenvía el código de verificación (regla anti-spam 1/min). Falla si el email ya está verificado.</summary>
    Task ReenviarCodigoAsync(string email, string negocioSlug, CancellationToken cancellationToken = default);

    /// <summary>Purga (borrado físico) los negocios con cuenta cerrada hace más de 16 días. Devuelve cuántos borró.</summary>
    Task<int> LimpiarCuentasCerradasAsync(CancellationToken cancellationToken = default);
}
