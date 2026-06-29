namespace OrbIT.Application.Auth;

/// <summary>
/// Implementación de <see cref="IPasswordHasher"/> con BCrypt.Net-Next.
/// Work factor 10: el mismo default que usaba bcrypt de Node en el NestJS de producción,
/// para que los hashes existentes verifiquen idéntico y no haya diferencia de costo.
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 10;

    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) =>
        BCrypt.Net.BCrypt.Verify(password, hash);
}
