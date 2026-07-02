namespace OrbIT.Api.Contracts.Auth;

// ── Google OAuth ───────────────────────────────────────────────────────────

/// <summary>Body de <c>POST /auth/google/exchange</c>: el OTT crudo recibido en el redirect del callback.</summary>
public sealed record GoogleExchangeRequest(string Ott);

/// <summary>Body de <c>POST /auth/google/registro</c>: datos del alta (email/nombre vienen del perfil de Google).</summary>
public sealed record GoogleRegistroRequest(string Email, string Nombre, string NombreNegocio, string Slug);
