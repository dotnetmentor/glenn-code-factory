using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Events;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;

namespace Source.Features.FlyManagement.Controllers;

/// <summary>
/// Public, anonymous webhook receiver for Fly.io machine-state notifications. Fly POSTs
/// every state transition (start, stop, suspend, crash, …) to a single endpoint we
/// register in the dashboard with a shared secret.
///
/// Pipeline (deliberately mirrors <c>GithubWebhooksController</c>):
///   1. Read the raw body bytes — we HMAC the *exact* bytes Fly sent, so re-serialising
///      via [FromBody] would normalise whitespace and break the signature.
///   2. Require the <c>Fly-Webhook-Signature</c> header (TODO: confirm exact header name
///      against the live Fly docs once a real delivery lands; the verification logic is
///      otherwise format-agnostic and tolerates a <c>sha256=</c> prefix).
///   3. Validate HMAC-SHA256 of the body against <see cref="FlyOptions.WebhookSecret"/>.
///   4. Idempotency: if we've already audited this <c>event_id</c> within the last 5
///      minutes, return 200 without re-publishing the domain event. The 5-minute window
///      covers Fly's at-least-once retry behaviour without bloating the audit table.
///   5. Persist a <see cref="FlyOperation"/> row tagged <c>Webhook:{event_type}</c>.
///   6. Publish <see cref="FlyMachineStateChanged"/> for downstream subscribers
///      (runtime-lifecycle feature, alerting, etc.).
/// </summary>
[ApiController]
[Route("api/webhooks/fly")]
[AllowAnonymous]
[Tags("Fly Webhooks")]
public class FlyWebhookController : ControllerBase
{
    private const string SignatureHeader = "Fly-Webhook-Signature";
    private const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Window during which a repeat <c>event_id</c> is treated as a duplicate delivery
    /// and short-circuits to 200. Matches Fly's documented retry window — beyond that
    /// the same id is vanishingly unlikely to recur and would imply a real new event.
    /// </summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IFlyOptionsAccessor _options;
    private readonly ApplicationDbContext _db;
    private readonly IPublisher _publisher;
    private readonly ILogger<FlyWebhookController> _logger;

    public FlyWebhookController(
        IFlyOptionsAccessor options,
        ApplicationDbContext db,
        IPublisher publisher,
        ILogger<FlyWebhookController> logger)
    {
        _options = options;
        _db = db;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Receive a Fly webhook delivery. Returns 200 on accept (or duplicate), 400 on a
    /// malformed body, 401 on signature failure, 500 if the secret is unconfigured.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(FlyWebhookAck), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<FlyWebhookAck>> Receive(CancellationToken ct)
    {
        // 1. Read raw body bytes verbatim — required for HMAC.
        byte[] body;
        await using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, ct);
            body = ms.ToArray();
        }

        // 2. Require the signature header.
        if (!Request.Headers.TryGetValue(SignatureHeader, out var sigValues) ||
            string.IsNullOrWhiteSpace(sigValues.ToString()))
        {
            _logger.LogWarning(
                "Fly webhook rejected: missing {Header}. IP={Ip}",
                SignatureHeader,
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        var providedSignature = sigValues.ToString().Trim();

        // 3. Verify HMAC against the configured secret. Empty secret is operator error,
        //    surface as 500 so it shows up in monitoring rather than silently dropping.
        var secret = _options.Current.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("Fly webhook rejected: WebhookSecret not configured in SystemSettings.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!VerifyHmac(secret, body, providedSignature))
        {
            _logger.LogWarning(
                "Fly webhook rejected: HMAC mismatch. IP={Ip}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        // 4. Parse payload. We only care about a handful of fields; unknown keys are ignored.
        FlyWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FlyWebhookPayload>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Fly webhook rejected: invalid JSON.");
            return BadRequest();
        }

        if (payload is null || string.IsNullOrEmpty(payload.EventId))
        {
            _logger.LogWarning("Fly webhook rejected: missing event_id.");
            return BadRequest();
        }

        // 5. Idempotency check. RequestKey carries a webhook: prefix so it cannot collide
        //    with the operation request keys FlyClient uses for outbound calls.
        var requestKey = $"webhook:{payload.EventId}";
        var since = DateTime.UtcNow - DedupWindow;
        var alreadySeen = await _db.FlyOperations
            .AsNoTracking()
            .AnyAsync(o => o.RequestKey == requestKey && o.CreatedAt >= since, ct);
        if (alreadySeen)
        {
            _logger.LogInformation("Fly webhook deduped: event_id={EventId}", payload.EventId);
            return Ok(new FlyWebhookAck { Status = "duplicate", EventType = payload.EventType ?? string.Empty });
        }

        // 6. Persist the audit row. CreatedAt/UpdatedAt are stamped by the IAuditable
        //    interceptor — do NOT set them manually (see backend CLAUDE.md rules).
        var rawJson = Encoding.UTF8.GetString(body);
        _db.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = $"Webhook:{payload.EventType ?? "unknown"}",
            RequestKey = requestKey,
            RequestPayload = rawJson,
            Status = FlyOperationStatus.Succeeded,
            HttpStatusCode = 200,
        });
        await _db.SaveChangesAsync(ct);

        // 7. Publish the domain event. IPublisher is MediatR's Publish-only facet —
        //    handlers run after this returns; failures don't roll back the audit row.
        await _publisher.Publish(new FlyMachineStateChanged(
            MachineId: payload.MachineId ?? string.Empty,
            NewState: payload.NewState ?? string.Empty,
            PreviousState: payload.PreviousState,
            OccurredAt: payload.OccurredAt ?? DateTime.UtcNow,
            FlyEventId: payload.EventId), ct);

        _logger.LogInformation(
            "Fly webhook accepted: event_id={EventId}, machine={Machine}, new_state={State}",
            payload.EventId, payload.MachineId, payload.NewState);

        return Ok(new FlyWebhookAck { Status = "ok", EventType = payload.EventType ?? string.Empty });
    }

    /// <summary>
    /// Constant-time HMAC verification. Tolerates an optional <c>sha256=</c> prefix so the
    /// same code works whether Fly sends the bare hex or a GitHub-style namespaced form
    /// (the docs aren't explicit; the prefix is harmless to accept).
    /// </summary>
    private static bool VerifyHmac(string secret, byte[] body, string providedSignature)
    {
        var providedHex = providedSignature.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)
            ? providedSignature[SignaturePrefix.Length..]
            : providedSignature;
        if (providedHex.Length == 0) return false;

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(body);

        if (providedBytes.Length != computed.Length) return false;
        return CryptographicOperations.FixedTimeEquals(providedBytes, computed);
    }
}

/// <summary>
/// Inbound Fly webhook payload. Field names use snake_case per Fly's conventions; the
/// schema here captures only the fields we currently consume — extra keys are tolerated.
/// </summary>
public record FlyWebhookPayload(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("machine_id")] string? MachineId,
    [property: JsonPropertyName("previous_state")] string? PreviousState,
    [property: JsonPropertyName("new_state")] string? NewState,
    [property: JsonPropertyName("occurred_at")] DateTime? OccurredAt);

/// <summary>Tiny ack body so Swagger emits a concrete 200 type for the frontend.</summary>
public sealed record FlyWebhookAck
{
    public required string Status { get; init; }
    public required string EventType { get; init; }
}
