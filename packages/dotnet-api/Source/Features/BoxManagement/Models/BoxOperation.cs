using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Append-only audit row capturing a single HTTP call we made against the Box
/// API (box.ascii.dev). Every operation we issue — fork box, stop, resume,
/// delete, set TTL, run command, etc. — gets one row here so we can:
///
/// <list type="bullet">
///   <item>trace what happened to a given runtime over time;</item>
///   <item>look up an existing result by <see cref="RequestKey"/> to make
///         retries idempotent;</item>
///   <item>diagnose failures with the full request and response payloads
///         already on hand.</item>
/// </list>
///
/// <para>Deliberately NOT soft-deletable — these rows are the audit trail and
/// must never disappear. They also intentionally stay a thin POCO; all behaviour
/// lives in the <see cref="BoxClient"/> and handlers that write the rows.</para>
/// </summary>
public class BoxOperation : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// The runtime this operation targets. Nullable because some operations are
    /// runtime-agnostic (e.g. <c>ListBoxes</c>, account-level lookups).
    /// </summary>
    public Guid? RuntimeId { get; set; }

    /// <summary>
    /// Logical operation name, e.g. <c>"ForkBox"</c>, <c>"StopBox"</c>,
    /// <c>"DeleteBox"</c>. Free-form so new Box verbs don't require a schema change.
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Idempotency key, e.g. <c>"fork-box:{runtimeId}"</c>. Multiple rows can
    /// share a key — the latest succeeded row wins on lookup. <c>null</c> when an
    /// operation isn't safe to dedupe (typically read-only calls).
    /// </summary>
    public string? RequestKey { get; set; }

    /// <summary>JSON of the body / arguments we sent to Box.</summary>
    public string RequestPayload { get; set; } = string.Empty;

    /// <summary>JSON of the response Box returned. <c>null</c> while still pending or on transport failure.</summary>
    public string? ResponsePayload { get; set; }

    /// <summary>HTTP status code returned by Box. <c>null</c> if no response was received.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>Lifecycle state — see <see cref="BoxOperationStatus"/>. Persisted as a string.</summary>
    public BoxOperationStatus Status { get; set; }

    /// <summary>
    /// Short machine-readable error code (e.g. <c>"box_starting"</c>, <c>"rate_limited"</c>,
    /// <c>"daily_limit_reached"</c>). Sourced either from the Box response body or a
    /// synthetic value the client applies for transport-level failures.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Wall-clock latency of the HTTP call in milliseconds. <c>null</c> while pending.</summary>
    public int? LatencyMs { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
