using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Commands;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;
using Source.Shared.Controllers;

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Public, anonymous webhook receiver for the GitHub App. GitHub posts every event
/// (installation, installation_repositories, push, pull_request, …) to this single endpoint.
///
/// Pipeline:
///   1. Read the raw bytes of the request body — the validator HMACs the *exact* bytes GitHub
///      sent, so we deliberately bypass model binding ([FromBody] re-serialises and breaks the HMAC).
///   2. Require the three GitHub headers (X-GitHub-Event, X-GitHub-Delivery, X-Hub-Signature-256).
///   3. Validate the HMAC against the configured webhook secret.
///   4. Idempotency: if we've seen this DeliveryId before, return 200 without re-dispatching.
///   5. Persist a <see cref="GithubWebhookDelivery"/> row BEFORE dispatching so a partial failure
///      still records the delivery (subsequent retries from GitHub will short-circuit at step 4).
///   6. Dispatch to a per-event MediatR command.
/// </summary>
[ApiController]
[Route("api/github/webhooks")]
[AllowAnonymous]
[Tags("GitHub Webhooks")]
public class GithubWebhooksController : BaseApiController
{
    private const string EventHeader = "X-GitHub-Event";
    private const string DeliveryHeader = "X-GitHub-Delivery";
    private const string SignatureHeader = "X-Hub-Signature-256";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _db;
    private readonly IGithubWebhookValidator _validator;

    public GithubWebhooksController(
        IMediator mediator,
        ILogger<GithubWebhooksController> logger,
        ApplicationDbContext db,
        IGithubWebhookValidator validator)
        : base(mediator, logger)
    {
        _db = db;
        _validator = validator;
    }

    /// <summary>
    /// Receives a webhook delivery from GitHub. Returns 200 on success (or duplicate delivery),
    /// 400 on missing/malformed headers, 401 on signature mismatch.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(GithubWebhookAck), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<GithubWebhookAck>> Receive(CancellationToken cancellationToken)
    {
        // 1. Required headers.
        var eventName = Request.Headers[EventHeader].ToString();
        var deliveryId = Request.Headers[DeliveryHeader].ToString();
        var signature = Request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrWhiteSpace(eventName) ||
            string.IsNullOrWhiteSpace(deliveryId) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return BadRequest(new { error = "Missing required GitHub webhook headers." });
        }

        // 2. Read raw body bytes. We must HMAC the bytes verbatim — re-serialising via [FromBody]
        //    would normalise whitespace and break the signature.
        byte[] body;
        await using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, cancellationToken);
            body = ms.ToArray();
        }

        // 3. HMAC validation.
        if (!_validator.Validate(signature, body))
        {
            Logger.LogWarning("Rejecting GitHub webhook delivery {DeliveryId}: invalid signature.", deliveryId);
            return Unauthorized(new { error = "Invalid webhook signature." });
        }

        // 4. Idempotency.
        var alreadyProcessed = await _db.GithubWebhookDeliveries
            .AsNoTracking()
            .AnyAsync(d => d.DeliveryId == deliveryId, cancellationToken);
        if (alreadyProcessed)
        {
            Logger.LogInformation(
                "Skipping duplicate GitHub webhook delivery {DeliveryId} (event={Event}).",
                deliveryId, eventName);
            return Ok(new GithubWebhookAck { Status = "duplicate", Event = eventName });
        }

        // Best-effort parse of the action — used both for the delivery row and for routing.
        // We don't fail the webhook if the body isn't JSON; we just store action=null.
        string? action = TryReadActionField(body);

        // 5. Persist the delivery FIRST so future retries are deduped even if dispatch throws.
        _db.GithubWebhookDeliveries.Add(new GithubWebhookDelivery
        {
            Id = Guid.NewGuid(),
            DeliveryId = deliveryId,
            Event = eventName,
            Action = action,
        });
        await _db.SaveChangesAsync(cancellationToken);

        // 6. Dispatch by event type. Unknown events are accepted + logged so GitHub's delivery
        //    dashboard stays green and the operator can see them in our logs.
        var rawJson = System.Text.Encoding.UTF8.GetString(body);
        switch (eventName.ToLowerInvariant())
        {
            case "installation":
            {
                var payload = TryDeserialize<GithubInstallationWebhookPayload>(body);
                if (payload is null) break;
                await Mediator.Send(new HandleGithubInstallationEventCommand(payload), cancellationToken);
                break;
            }
            case "installation_repositories":
            {
                var payload = TryDeserialize<GithubInstallationRepositoriesWebhookPayload>(body);
                if (payload is null) break;
                await Mediator.Send(new HandleGithubInstallationRepositoriesEventCommand(payload), cancellationToken);
                break;
            }
            case "push":
                await Mediator.Send(new HandleGithubPushEventCommand(deliveryId, rawJson), cancellationToken);
                break;
            case "pull_request":
                await Mediator.Send(new HandleGithubPullRequestEventCommand(deliveryId, rawJson), cancellationToken);
                break;
            default:
                Logger.LogInformation(
                    "Received GitHub webhook with unrecognised event {Event} (delivery {DeliveryId}); accepting + ignoring.",
                    eventName, deliveryId);
                break;
        }

        return Ok(new GithubWebhookAck { Status = "ok", Event = eventName });
    }

    /// <summary>
    /// Pulls just the top-level <c>action</c> field out of the body without forcing the whole
    /// payload through a typed binding. Returns null on parse failures or when the field is absent
    /// (some events — e.g. <c>push</c> — don't have an action).
    /// </summary>
    private static string? TryReadActionField(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("action", out var actionEl)
                   && actionEl.ValueKind == JsonValueKind.String
                ? actionEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private T? TryDeserialize<T>(byte[] body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Failed to deserialise webhook payload as {Type}; ignoring body.", typeof(T).Name);
            return null;
        }
    }
}

/// <summary>Tiny ack body so Swagger has something concrete to type the 200 response with.</summary>
public sealed record GithubWebhookAck
{
    public required string Status { get; init; }
    public required string Event { get; init; }
}
