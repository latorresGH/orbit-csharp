using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Auth;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// ── Multi-tenancy ─────────────────────────────────────────────────────────
// El tenant (negocio) activo se resuelve por request desde el HttpContext y se
// inyecta en el OrbitDbContext, que aplica global query filters por NegocioId.
// Scoped: una resolución de tenant por request, alineada con el scope del DbContext.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ── PostgreSQL: DataSource con los 11 enums CLR mapeados ──────────────────
// La cadena se lee de appsettings (placeholder) + appsettings.Development.json
// (valor real, gitignoreado). El DataSource registra los enums con el mismo
// ExactNameTranslator que usa OnModelCreating en Infrastructure, para que el
// mapeo CLR <-> tipo enum de PostgreSQL sea idéntico en runtime.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Falta la cadena de conexión 'DefaultConnection'. Definila en " +
        "appsettings.Development.json o en variables de entorno.");

var enumNameTranslator = new ExactNameTranslator();
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder
    .MapEnum<EstadoMesa>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoOferta>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoPago>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoPedido>(nameTranslator: enumNameTranslator)
    .MapEnum<MetodoPago>(nameTranslator: enumNameTranslator)
    .MapEnum<Role>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoMovimientoCaja>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoOferta>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoPedido>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoTurno>(nameTranslator: enumNameTranslator)
    .MapEnum<UnidadMedida>(nameTranslator: enumNameTranslator);
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<OrbitDbContext>(options =>
    options
        .UseNpgsql(dataSource, npgsql => npgsql.MapOrbitEnums())
        // En los procesos de test corren varios hosts/DataSources en paralelo (cada
        // WebApplicationFactory arma el suyo), y el contador de EF de "IServiceProvider
        // internos creados" es global al proceso, así que cruza el umbral de 20 y el
        // warning se escala a excepción. En producción hay un único host: este warning
        // no aplica nunca, por eso lo silenciamos sin perder señal real.
        .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

// ── Auth: JWT + servicios ─────────────────────────────────────────────────
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException(
        "Falta la sección 'Jwt'. Definila en appsettings.Development.json o en variables de entorno.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Conservamos los nombres de claim tal cual (sub, role, negocioId) sin el
        // remapeo por defecto de .NET; el HttpTenantProvider lee "negocioId" directo.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.AccessTokenSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };

        // El access token viaja en una cookie HttpOnly, no en el header Authorization
        // (replica el comportamiento del NestJS de producción).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// ── Rate limiting nativo: 5 intentos/min por IP en /auth/login ────────────
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Punto de entrada visible para WebApplicationFactory en los tests de integración.
public partial class Program;
