using System.ComponentModel.DataAnnotations;

namespace OrbIT.Api.Contracts.Negocios;

// ── Requests públicos ────────────────────────────────────────────────────────

public sealed class RegistroNegocioRequest
{
    [Required, MinLength(2)]
    public string NombreNegocio { get; set; } = null!;

    [Required, MinLength(3), MaxLength(30)]
    [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "El slug solo puede tener letras minúsculas, números y guiones")]
    public string Slug { get; set; } = null!;

    [Required, MinLength(2)]
    public string NombreAdmin { get; set; } = null!;

    [Required, EmailAddress(ErrorMessage = "Email inválido")]
    public string Email { get; set; } = null!;

    [Required, MinLength(8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres")]
    public string Password { get; set; } = null!;
}

public sealed class VerificarEmailRequest
{
    [Required] public string Email { get; set; } = null!;
    [Required] public string NegocioSlug { get; set; } = null!;
    [Required] public string Codigo { get; set; } = null!;
}

public sealed class ReenviarCodigoRequest
{
    [Required] public string Email { get; set; } = null!;
    [Required] public string NegocioSlug { get; set; } = null!;
}

// ── Requests autenticados ────────────────────────────────────────────────────

public sealed class ActualizarMiPerfilRequest
{
    public string? NombreNegocio { get; set; }
    public string? Slug { get; set; }
    public string? NombreAdmin { get; set; }
    public string? LogoUrl { get; set; }
}

// ── Requests SUPERADMIN ──────────────────────────────────────────────────────

public sealed class CrearNegocioRequest
{
    [Required, MinLength(2)]
    public string Nombre { get; set; } = null!;

    [Required]
    [RegularExpression("^[a-z0-9-]+$", ErrorMessage = "El slug solo puede tener letras minúsculas, números y guiones")]
    public string Slug { get; set; } = null!;

    [Required] public string AdminEmail { get; set; } = null!;
    [Required, MinLength(6)] public string AdminPassword { get; set; } = null!;
    [Required] public string AdminNombre { get; set; } = null!;
    public string? Plan { get; set; }
}

public sealed class ActualizarNegocioRequest
{
    public string? Nombre { get; set; }
    public string? Slug { get; set; }
    public bool? Activo { get; set; }
    public string? Plan { get; set; }
}

public sealed class ExtenderTrialRequest
{
    public int Dias { get; set; }
}

// ── Responses ────────────────────────────────────────────────────────────────

public sealed record RegistroPendienteResponse(bool EmailPendiente, string Email, string Slug);

public sealed record UsuarioResponse(string Id, string Email, string Nombre, string Role);

public sealed record SlugDisponibleResponse(bool Disponible);

public sealed record InfoPublicaResponse(string Id, string Nombre, string Slug, string? LogoUrl);

public sealed record MiEstadoResponse(string EstadoPlan, int? DiasRestantes, DateTime? TrialExpira);

public sealed record MiPerfilNegocio(
    string Id, string Nombre, string Slug, string? LogoUrl, string Plan,
    DateTime? TrialExpira, DateTime? CuentaCerradaAt, string EstadoPlan, int? DiasRestantes);

public sealed record MiPerfilResponse(MiPerfilNegocio Negocio, UsuarioResponse User);

public sealed record EstadoCierreResponse(bool Cerrada, int? DiasRestantes);

/// <summary>Item del listado SUPERADMIN: negocio + su admin + estado del plan.</summary>
public sealed record NegocioListItemResponse(
    string Id, string Nombre, string Slug, bool Activo, string Plan,
    DateTime? TrialExpira, DateTime? CuentaCerradaAt, DateTime CreatedAt,
    string EstadoPlan, NegocioAdminResponse? Admin);

public sealed record NegocioAdminResponse(string Id, string Nombre, string Email);

public sealed record NegocioDetalleResponse(
    string Id, string Nombre, string Slug, bool Activo, string Plan,
    DateTime? TrialExpira, DateTime? CuentaCerradaAt, string? LogoUrl, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record LimpiezaResponse(int Eliminados);
