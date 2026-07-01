using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MercadoPago.Client;
using MercadoPago.Client.Payment;
using MercadoPago.Client.Preapproval;
using MercadoPago.Client.Preference;
using OrbIT.Api.Billing;
using OrbIT.Api.Contracts.Billing;
using OrbIT.Application.Negocios;
using OrbIT.Domain.MultiTenancy;
using OrbIT.Infrastructure.Models;

namespace OrbIT.Api.Controllers;

/// <summary>
/// Billing / suscripciones vía MercadoPago (SDK oficial mercadopago-sdk). El ADMIN del negocio inicia un
/// checkout de su plan, consulta su suscripción y la cancela; MP notifica los pagos al webhook público.
/// El webhook es idempotente vía <see cref="WebhookEvent"/> (externalId único = id del pago) y valida la
/// firma <c>x-signature</c> de MP antes de tocar nada. Al aprobarse un pago se actualiza el <c>planId</c> del
/// negocio (tabla global <c>Plan</c>).
/// </summary>
[ApiController]
[Route("billing")]
[Authorize(Roles = "ADMIN")]
public sealed class BillingController : ControllerBase
{
    private const string PlanBasicoSlug = "basic";
    private const string MonedaArs = "ARS";

    private readonly OrbitDbContext _db;
    private readonly ITenantProvider _tenant;
    private readonly MercadoPagoSettings _mp;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        OrbitDbContext db,
        ITenantProvider tenant,
        IOptions<MercadoPagoSettings> mp,
        ILogger<BillingController> logger)
    {
        _db = db;
        _tenant = tenant;
        _mp = mp.Value;
        _logger = logger;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Checkout: genera la preferencia de pago del plan
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("checkout/{planSlug}")]
    public async Task<IActionResult> Checkout(string planSlug)
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId)) return Forbid();

        if (!_mp.TieneAccessToken)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "MercadoPago no está configurado (falta ACCESS_TOKEN)" });
        }

        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Slug == planSlug);
        if (plan is null) return NotFound(new { message = $"Plan \"{planSlug}\" no encontrado" });

        // Sin plan de MP asociado no se puede cobrar (paridad con el NestJS): 400.
        if (string.IsNullOrEmpty(plan.MpPlanId))
        {
            return BadRequest(new { message = $"El plan \"{planSlug}\" no tiene un plan de MercadoPago asociado" });
        }

        var request = new PreferenceRequest
        {
            Items = new List<PreferenceItemRequest>
            {
                new()
                {
                    Id = plan.Id,
                    Title = plan.Nombre,
                    Description = $"Suscripción mensual Orb.IT — {plan.Nombre}",
                    Quantity = 1,
                    CurrencyId = MonedaArs,
                    UnitPrice = (decimal)plan.PrecioMensual,
                },
            },
            // ExternalReference viaja al pago (a diferencia de Metadata): el webhook lo lee para reconciliar.
            ExternalReference = $"{negocioId}:{plan.Slug}",
            Metadata = new Dictionary<string, object>
            {
                ["negocio_id"] = negocioId,
                ["plan_slug"] = plan.Slug,
                ["mp_plan_id"] = plan.MpPlanId,
            },
        };

        if (!string.IsNullOrWhiteSpace(_mp.NotificationUrl)) request.NotificationUrl = _mp.NotificationUrl;
        if (!string.IsNullOrWhiteSpace(_mp.BackUrlSuccess) || !string.IsNullOrWhiteSpace(_mp.BackUrlFailure) || !string.IsNullOrWhiteSpace(_mp.BackUrlPending))
        {
            request.BackUrls = new PreferenceBackUrlsRequest
            {
                Success = _mp.BackUrlSuccess,
                Failure = _mp.BackUrlFailure,
                Pending = _mp.BackUrlPending,
            };
            if (!string.IsNullOrWhiteSpace(_mp.BackUrlSuccess)) request.AutoReturn = "approved";
        }

        try
        {
            var client = new PreferenceClient();
            var pref = await client.CreateAsync(request, RequestOpts());
            return Ok(new CheckoutResponse(pref.Id, pref.InitPoint, pref.SandboxInitPoint));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creando preferencia de MercadoPago para negocio {NegocioId} plan {PlanSlug}", negocioId, planSlug);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "No se pudo generar el checkout en MercadoPago" });
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Webhook público de MercadoPago
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook()
    {
        // Identificamos el recurso: MP manda ?type=payment&data.id=123 (query) y/o un body JSON equivalente.
        var (tipo, dataId) = await LeerNotificacionAsync();

        // ── Validación de firma x-signature ──────────────────────────────────
        // TODO(firma-MP): el SDK oficial (mercadopago-sdk 3.3.0) NO expone ningún helper para validar la
        // firma del webhook — hay que implementarla a mano. El algoritmo documentado por MercadoPago es:
        //   1. Header `x-signature` = "ts=<timestamp>,v1=<hmac_hex>"; header `x-request-id` = "<uuid>".
        //   2. Manifest (plantilla EXACTA, con el ';' final): `id:<data.id>;request-id:<x-request-id>;ts:<ts>;`
        //      (si data.id es alfanumérico, MP lo espera en minúsculas; para ids de pago numéricos no cambia).
        //   3. HMAC-SHA256(manifest, <secret del panel de MP>) en hex debe coincidir con v1 (comparación
        //      en tiempo constante). Ver ValidarFirmaWebhook. Si algún día MP publica un validador en el SDK,
        //      reemplazar esta implementación por el helper oficial.
        if (!ValidarFirmaWebhook(dataId))
        {
            _logger.LogWarning("Webhook MP con firma inválida (data.id={DataId})", dataId);
            return Unauthorized(new { message = "Firma inválida" });
        }

        // Sólo nos interesan las notificaciones de pago; el resto se ACK-ean sin procesar.
        if (!string.Equals(tipo, "payment", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(dataId))
        {
            return Ok(new { received = true });
        }

        if (!long.TryParse(dataId, out var paymentId))
        {
            return Ok(new { received = true });
        }

        // Idempotencia: si ya procesamos este pago, no repetimos (WebhookEvent.externalId es único).
        var externalId = $"payment:{paymentId}";
        if (await _db.WebhookEvents.AnyAsync(w => w.ExternalId == externalId))
        {
            return Ok(new { received = true, duplicated = true });
        }

        try
        {
            var payment = await new PaymentClient().GetAsync(paymentId, RequestOpts());

            // Sólo un pago aprobado activa el plan. Reconciliamos por ExternalReference = "negocioId:planSlug".
            if (string.Equals(payment.Status, "approved", StringComparison.OrdinalIgnoreCase)
                && TryParseExternalReference(payment.ExternalReference, out var negocioId, out var planSlug))
            {
                var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Slug == planSlug);
                var negocio = await _db.Negocios.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.Id == negocioId);
                if (plan is not null && negocio is not null)
                {
                    negocio.PlanId = plan.Id;
                    negocio.UpdatedAt = Now();
                }
            }

            // Registramos el evento aunque el pago no esté aprobado: dedupe de reintentos de MP sobre el mismo id.
            _db.WebhookEvents.Add(new WebhookEvent
            {
                Id = Guid.NewGuid().ToString(),
                ExternalId = externalId,
                CreatedAt = Now(),
            });
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Carrera con otra entrega del mismo webhook: el índice único de externalId ya lo marcó procesado.
            return Ok(new { received = true, duplicated = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando webhook de pago {PaymentId}", paymentId);
            // 500 → MP reintenta; no marcamos procesado para no perder el pago.
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error procesando el webhook" });
        }

        return Ok(new { received = true });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Suscripción actual / cancelación
    // ═════════════════════════════════════════════════════════════════════════

    [HttpGet("mi-suscripcion")]
    public async Task<IActionResult> MiSuscripcion()
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId)) return Forbid();

        var negocio = await _db.Negocios
            .Include(n => n.PlanNavigation)
            .FirstOrDefaultAsync(n => n.Id == negocioId);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        var estado = PlanEstado.Calcular(negocio.Activo, negocio.Plan, negocio.TrialExpira);
        var plan = negocio.PlanNavigation is { } p
            ? new PlanSuscripcionResponse(
                p.Id, p.Nombre, p.Slug, p.PrecioMensual, p.LimiteProductos, p.LimiteUsuarios,
                p.TieneMesas, p.TieneImagenes, p.TieneSignalR, p.TieneReportes,
                p.TieneToppingGrupos, p.TieneOfertas, p.TieneInsumos)
            : null;

        return Ok(new MiSuscripcionResponse(negocioId, estado, plan, negocio.MpSuscripcionId));
    }

    [HttpPost("cancelar")]
    public async Task<IActionResult> Cancelar()
    {
        var negocioId = _tenant.NegocioId;
        if (string.IsNullOrEmpty(negocioId)) return Forbid();

        var negocio = await _db.Negocios.FirstOrDefaultAsync(n => n.Id == negocioId);
        if (negocio is null) return NotFound(new { message = "Negocio no encontrado" });

        // Cancelar la suscripción en MP (si existe y MP está configurado). Best-effort: si MP falla, igual
        // bajamos el plan localmente para no dejar al negocio “cobrando” un plan que quiso cancelar.
        var canceladoEnMp = false;
        if (!string.IsNullOrEmpty(negocio.MpSuscripcionId) && _mp.TieneAccessToken)
        {
            try
            {
                await new PreapprovalClient().UpdateAsync(
                    negocio.MpSuscripcionId,
                    new PreapprovalUpdateRequest { Status = "cancelled" },
                    RequestOpts());
                canceladoEnMp = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo cancelar la suscripción MP {SuscripcionId} del negocio {NegocioId}", negocio.MpSuscripcionId, negocioId);
            }
        }

        // Bajar al plan basic.
        var basico = await _db.Plans.FirstOrDefaultAsync(p => p.Slug == PlanBasicoSlug);
        negocio.PlanId = basico?.Id;
        negocio.UpdatedAt = Now();
        await _db.SaveChangesAsync();

        return Ok(new CancelarSuscripcionResponse(true, PlanBasicoSlug, canceladoEnMp));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private RequestOptions RequestOpts() => new() { AccessToken = _mp.AccessToken };

    /// <summary>Lee (type, data.id) del webhook: primero de la query (?type=&amp;data.id=), luego del body JSON.</summary>
    private async Task<(string? Tipo, string? DataId)> LeerNotificacionAsync()
    {
        var tipo = Request.Query["type"].ToString();
        var dataId = Request.Query["data.id"].ToString();
        if (string.IsNullOrEmpty(tipo)) tipo = Request.Query["topic"].ToString();
        if (string.IsNullOrEmpty(dataId)) dataId = Request.Query["id"].ToString();

        if (!string.IsNullOrEmpty(tipo) && !string.IsNullOrEmpty(dataId))
        {
            return (tipo, dataId);
        }

        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(tipo) && root.TryGetProperty("type", out var t)) tipo = t.GetString();
                if (string.IsNullOrEmpty(dataId) && root.TryGetProperty("data", out var d) && d.TryGetProperty("id", out var idEl))
                {
                    dataId = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString();
                }
            }
            catch (JsonException) { /* body no-JSON: quedamos con lo de la query */ }
        }

        return (string.IsNullOrEmpty(tipo) ? null : tipo, string.IsNullOrEmpty(dataId) ? null : dataId);
    }

    /// <summary>
    /// Valida la firma <c>x-signature</c> de MercadoPago (ver TODO(firma-MP) en <see cref="Webhook"/>).
    /// Fail-closed: sin secret configurado o sin header válido, la firma se considera inválida.
    /// </summary>
    private bool ValidarFirmaWebhook(string? dataId)
    {
        if (!_mp.TieneWebhookSecret) return false;

        var signature = Request.Headers["x-signature"].ToString();
        var requestId = Request.Headers["x-request-id"].ToString();
        if (string.IsNullOrEmpty(signature)) return false;

        string? ts = null, v1 = null;
        foreach (var part in signature.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var clave = kv[0].Trim();
            var valor = kv[1].Trim();
            if (clave == "ts") ts = valor;
            else if (clave == "v1") v1 = valor;
        }
        if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(v1)) return false;

        // data.id alfanumérico → minúsculas (regla MP); numérico queda igual.
        var idParaManifest = dataId?.ToLowerInvariant() ?? string.Empty;
        var manifest = $"id:{idParaManifest};request-id:{requestId};ts:{ts};";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_mp.WebhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(v1.ToLowerInvariant()));
    }

    private static bool TryParseExternalReference(string? externalReference, out string negocioId, out string planSlug)
    {
        negocioId = string.Empty;
        planSlug = string.Empty;
        if (string.IsNullOrEmpty(externalReference)) return false;

        var partes = externalReference.Split(':', 2);
        if (partes.Length != 2 || partes[0].Length == 0 || partes[1].Length == 0) return false;

        negocioId = partes[0];
        planSlug = partes[1];
        return true;
    }

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
}
