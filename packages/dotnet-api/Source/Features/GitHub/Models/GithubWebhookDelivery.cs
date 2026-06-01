using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.GitHub.Models;

/// <summary>
/// One row per validated webhook delivery, recorded for idempotency.
/// The <see cref="DeliveryId"/> mirrors the <c>X-GitHub-Delivery</c> header GitHub
/// includes on every webhook — checking for an existing row before processing
/// guarantees we never apply the same event twice if GitHub retries.
/// </summary>
public class GithubWebhookDelivery : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>The <c>X-GitHub-Delivery</c> header value. Globally unique.</summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>The <c>X-GitHub-Event</c> header value (e.g. "push", "installation").</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>The optional sub-action from the body (e.g. "created", "deleted") — null when absent.</summary>
    public string? Action { get; set; }

    /// <summary><see cref="CreatedAt"/> doubles as the delivery's "ReceivedAt" timestamp.</summary>
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
