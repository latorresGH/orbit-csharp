using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text.Json;
using OrbIT.Api.Auth;
using OrbIT.Api.Billing;
using OrbIT.Api.Hubs;
using OrbIT.Api.MultiTenancy;
using OrbIT.Application.Audit;
using OrbIT.Application.Auth;
using OrbIT.Application.Caja;
using OrbIT.Application.CodigosDescuento;
using OrbIT.Application.Dashboard;
using OrbIT.Application.Demora;
using OrbIT.Application.Email;
using OrbIT.Application.Negocios;
using OrbIT.Application.Ofertas;
using OrbIT.Application.Pedidos;
using OrbIT.Application.Planes;
using OrbIT.Application.Turnos;
using OrbIT.Domain.Enums;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// ── CORS ──────────────────────────────────────────────────────────────────
// No había política de CORS en el proyecto. La agrego modelada en la del gateway NestJS (FRONTEND_URL +
// credentials:true): el handshake de SignalR viaja con la cookie HttpOnly access_token, así que necesita
// orígenes explícitos + AllowCredentials (incompatible con AllowAnyOrigin). Los orígenes se leen de
// "Cors:AllowedOrigins" (array) en appsettings; default localhost:3000 para desarrollo.
const string CorsPolicy = "orbit-frontend";
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
{
    corsOrigins = new[] { "http://localhost:3000" };
}
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// ── Multi-tenancy ─────────────────────────────────────────────────────────
// El tenant (negocio) activo se resuelve por request desde el HttpContext y se
// inyecta en el OrbitDbContext, que aplica global query filters por NegocioId.
// Scoped: una resolución de tenant por request, alineada con el scope del DbContext.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();
// Tenant resuelto fuera de banda (slug) para endpoints públicos: un holder scoped que el
// HttpTenantProvider consulta primero, y el resource filter que lo completa. Ver
// [AllowAnonymousWithTenant] y la sección "Endpoints públicos" de CLAUDE.md.
builder.Services.AddScoped<TenantResolutionContext>();
builder.Services.AddScoped<ResolveTenantBySlugFilter>();

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
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();

// ── Auditoría ─────────────────────────────────────────────────────────────
// Servicio transversal de AuditLog, scoped al request (comparte el DbContext del
// controller que lo inyecta). Primer consumidor: ProductoController (CAMBIO_PRECIO).
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

// ── Ofertas y códigos de descuento ───────────────────────────────────────
// Servicios reutilizables (scoped, comparten el DbContext del request) que consumen los endpoints
// públicos de cálculo/validación y, más adelante, crearPedido del PedidosController.
builder.Services.AddScoped<IOfertasCalculatorService, OfertasCalculatorService>();
builder.Services.AddScoped<ICodigosDescuentoService, CodigosDescuentoService>();

// ── Pedidos ───────────────────────────────────────────────────────────────
// PedidoService orquesta crear + cancelar (transaccional, stock). DemoraService es un stub null en
// Tanda A (best-effort, ver IDemoraService).
builder.Services.AddScoped<IDemoraService, DemoraServiceStub>();
builder.Services.AddScoped<IPedidoService, PedidoService>();

// ── Tiempo real (SignalR / PedidosHub) ────────────────────────────────────
// El PedidoService emite 'nuevo-pedido' a la room = negocioId vía IPedidoNotificationService (abstracción en
// Application; la impl PedidosNotificationService envuelve IHubContext<PedidosHub> acá en la capa Api). El
// protocolo JSON se serializa en camelCase para replicar el contrato del socket.io del NestJS.
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddScoped<IPedidoNotificationService, PedidosNotificationService>();

// ── Caja y Turnos ─────────────────────────────────────────────────────────
// TurnoService orquesta abrir + cerrar (transaccional, con cálculo de ventas/efectivo esperado); el turno es
// GLOBAL por negocio (un único activo, no uno por empleado). CajaService orquesta el registro de pago de un
// pedido y el batch (transaccional). Ambos son scoped y comparten el DbContext del request.
builder.Services.AddScoped<ITurnoService, TurnoService>();
builder.Services.AddScoped<ICajaService, CajaService>();

// ── Dashboard ─────────────────────────────────────────────────────────────
// DashboardService agrega métricas del negocio con GroupBy/Sum server-side (reemplaza el getMetrics
// monolítico de NestJS). Reutiliza ITurnoService para el turno activo del resumen-hoy; el resto son queries
// propias. Scoped, comparte el DbContext del request.
builder.Services.AddScoped<IDashboardService, DashboardService>();

// ── Negocio (onboarding / lifecycle) ──────────────────────────────────────
// NegocioService orquesta registro/verificación/alta-manual/purga (transaccional). IEmailService es un stub
// que loguea el código (SMTP real pendiente). SessionIssuer centraliza la emisión de cookies/refresh, usada
// por AuthController y por verificar-email.
builder.Services.AddScoped<IEmailService, EmailServiceStub>();
builder.Services.AddScoped<INegocioService, NegocioService>();
builder.Services.AddScoped<ISessionIssuer, SessionIssuer>();

// ── Planes / Billing ──────────────────────────────────────────────────────
// IPlanGuard valida límites (productos/usuarios) y features del plan contratado. Registrado en DI pero
// todavía NO enganchado a ningún controller existente (se aplicará en una iteración posterior). El
// BillingController usa MercadoPago con el ACCESS_TOKEN de configuración (sección "MercadoPago").
builder.Services.AddScoped<IPlanGuard, PlanGuard>();
builder.Services.Configure<MercadoPagoSettings>(builder.Configuration.GetSection(MercadoPagoSettings.SectionName));

// El SDK de MercadoPago usa un access token estático global. Lo seteamos al arrancar si hay un valor real
// configurado (en tests/CI queda el placeholder → no se toca y las llamadas a MP nunca se ejecutan porque
// los endpoints de billing requieren ADMIN). Cada request igual pasa RequestOptions con el token explícito.
var mpSettings = builder.Configuration.GetSection(MercadoPagoSettings.SectionName).Get<MercadoPagoSettings>();
if (mpSettings?.TieneAccessToken == true)
{
    MercadoPago.Config.MercadoPagoConfig.AccessToken = mpSettings.AccessToken;
}

var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException(
        "Falta la sección 'Jwt'. Definila en appsettings.Development.json o en variables de entorno.");

// ── Google OAuth ───────────────────────────────────────────────────────────
// El handler nativo maneja el anti-CSRF del roundtrip (correlation cookie + state protegido con
// DataProtection): NO hace falta el store de CSRF que el NestJS tenía en memoria. El slug (a qué negocio
// loguear) viaja en AuthenticationProperties.Items. El único store que queda es el OTT (tabla TempToken).
// Sólo se registra el handler si hay credenciales reales, así los tests/CI arrancan sin credenciales de Google.
builder.Services.Configure<GoogleSettings>(builder.Configuration.GetSection(GoogleSettings.SectionName));
var googleSettings = builder.Configuration.GetSection(GoogleSettings.SectionName).Get<GoogleSettings>() ?? new GoogleSettings();

// TODO: registrar un IHostedService que borre periódicamente los TempToken expirados
// (WHERE expiresAt < now), reemplazando el setInterval en memoria del NestJS. Por ahora no es urgente: el
// exchange ya ignora los expirados/usados (WHERE usada = false AND expiresAt > now), así que un token vencido
// nunca es válido; sólo quedan filas muertas acumulándose hasta que se implemente la limpieza.

var authBuilder = builder.Services
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

            // Gate por-request (réplica de jwt.strategy de NestJS): rechaza usuarios desactivados o con email
            // sin verificar. IgnoreQueryFilters porque el tenant aún no está resuelto en este punto.
            OnTokenValidated = async context =>
            {
                var sub = context.Principal?.FindFirst("sub")?.Value;
                if (string.IsNullOrEmpty(sub))
                {
                    context.Fail("Token inválido");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<OrbitDbContext>();
                var estado = await db.Users.IgnoreQueryFilters()
                    .Where(u => u.Id == sub)
                    .Select(u => new { u.Activo, u.EmailVerificado })
                    .FirstOrDefaultAsync();

                // emailVerificado null (usuarios previos a la feature) pasa; solo false estricto bloquea.
                if (estado is null || !estado.Activo || estado.EmailVerificado == false)
                {
                    context.Fail("Usuario no autorizado");
                }
            },
        };
    });

// Cookie externa temporal: sostiene la correlación OAuth + la identidad de Google entre el challenge y el
// callback. No es la sesión de la app (esa va por access/refresh); se limpia apenas se lee en el callback.
authBuilder.AddCookie(GoogleOAuth.ExternalScheme, options =>
{
    options.Cookie.Name = "orbit.external";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
});

if (googleSettings.TieneCredenciales)
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleSettings.ClientId;
        options.ClientSecret = googleSettings.ClientSecret;
        options.CallbackPath = googleSettings.CallbackPath;
        options.SignInScheme = GoogleOAuth.ExternalScheme;
        options.SaveTokens = false; // no necesitamos los tokens de Google, sólo email+nombre del perfil.
    });
}

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

    // Protege el endpoint público más costoso del sistema (POST /pedidos): 5 req/min por IP.
    options.AddPolicy("pedidos-create", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Registro público de negocios: 5 req/min por IP.
    options.AddPolicy("registro", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Reenvío de código de verificación: 3 req cada 5 min por IP (equivale al @Throttle del NestJS).
    options.AddPolicy("reenviar-codigo", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(5),
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

// CORS antes de auth y del ruteo del hub: el preflight/handshake de SignalR necesita los headers de CORS
// resueltos con AllowCredentials para que el navegador mande la cookie access_token en el WebSocket.
app.UseCors(CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<PedidosHub>("/hubs/pedidos");

app.Run();

// Punto de entrada visible para WebApplicationFactory en los tests de integración.
public partial class Program;
