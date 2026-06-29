namespace OrbIT.Application.Auth;

/// <summary>
/// Hashing y verificación de contraseñas. La implementación usa BCrypt con el mismo
/// work factor que el sistema NestJS de producción para no alterar el comportamiento.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}
