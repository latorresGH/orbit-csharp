using OrbIT.Domain.Enums;

namespace OrbIT.Api.Contracts.Usuarios;

/// <summary>
/// Alta de un empleado (TRABAJADOR o DELIVERY) dentro del negocio del ADMIN autenticado. El rol se restringe
/// a esos dos valores: no se puede crear otro ADMIN ni un SUPERADMIN por esta vía. La cuenta queda con email
/// verificado (la crea el ADMIN, no requiere el flujo de código).
/// </summary>
public sealed record CrearUsuarioRequest(string Email, string Password, string Nombre, Role Rol);

/// <summary>Toggle de activación de un usuario del negocio.</summary>
public sealed record ToggleUsuarioActivoRequest(bool Activo);

/// <summary>Vista de un usuario del negocio (sin password ni datos sensibles de verificación).</summary>
public sealed record UsuarioListItemResponse(
    string Id,
    string Email,
    string Nombre,
    string Rol,
    bool Activo,
    DateTime CreatedAt);
