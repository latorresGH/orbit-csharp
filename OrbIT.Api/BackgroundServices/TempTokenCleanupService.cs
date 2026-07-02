using Microsoft.EntityFrameworkCore;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.BackgroundServices;

/// <summary>
/// Limpieza periódica de la tabla <c>TempToken</c>: borra las filas ya usadas o expiradas. Reemplaza el
/// <c>setInterval</c> en memoria del NestJS por un <see cref="BackgroundService"/> que sobrevive reinicios y
/// es seguro con varias instancias (el DELETE es idempotente). No afecta la corrección del canje —el exchange
/// ya ignora usados/expirados (<c>WHERE usada = false AND expiresAt &gt; now</c>)—, sólo evita que se acumulen
/// filas muertas.
///
/// <para>Corre cada hora con un <see cref="PeriodicTimer"/>. Como es un singleton (todo IHostedService lo es),
/// resuelve el <see cref="OrbitDbContext"/> (scoped) desde un scope propio por tick vía
/// <see cref="IServiceScopeFactory"/>. El DELETE va con <c>IgnoreQueryFilters</c> porque <c>TempToken</c> es
/// tabla de sistema sin Global Query Filter y el barrido no corre dentro de un tenant resuelto.</para>
/// </summary>
public sealed class TempTokenCleanupService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TempTokenCleanupService> _logger;

    public TempTokenCleanupService(IServiceScopeFactory scopeFactory, ILogger<TempTokenCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un barrido inmediato al arrancar (por si quedaron filas de una corrida anterior) y luego cada hora.
        using var timer = new PeriodicTimer(Intervalo);
        do
        {
            await LimpiarAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task LimpiarAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            // Columnas "timestamp without time zone": Npgsql exige Kind=Unspecified para comparar (mismo patrón
            // que GoogleAuthService.Now()).
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            var borrados = await db.TempTokens
                .IgnoreQueryFilters()
                .Where(t => t.Usada || t.ExpiresAt < now)
                .ExecuteDeleteAsync(ct);

            if (borrados > 0)
            {
                _logger.LogInformation("[TempTokenCleanup] {Cantidad} token(s) temporal(es) limpiados.", borrados);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: no es un error, se corta el barrido y salimos.
        }
        catch (Exception ex)
        {
            // Best-effort: una falla del barrido no debe tumbar el host; se reintenta en el próximo tick.
            _logger.LogError(ex, "[TempTokenCleanup] Error limpiando TempTokens; se reintenta en el próximo ciclo.");
        }
    }
}
