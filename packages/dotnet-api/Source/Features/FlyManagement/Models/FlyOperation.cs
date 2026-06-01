using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Append-only audit row capturing a single HTTP call we made against the Fly.io
/// machines API (<c>api.machines.dev</c>). Every operation we issue — create
/// machine, suspend machine, destroy volume, list apps, etc. — gets one row here
/// so we can:
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
/// lives in the FlyClient and handlers that write the rows.</para>
/// </summary>
public class FlyOperation : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// The runtime this operation targets. Nullable because some operations are
    /// runtime-agnostic (e.g. <c>ListApps</c>, organisation-level lookups).
    /// </summary>
    public Guid? RuntimeId { get; set; }

    /// <summary>
    /// Logical operation name, e.g. <c>"CreateMachine"</c>, <c>"SuspendMachine"</c>,
    /// <c>"DestroyVolume"</c>. Free-form so new Fly verbs don't require a schema change.
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Idempotency key, e.g. <c>"machineCreate:{runtimeId}"</c>. Multiple rows can
    /// share a key — the latest succeeded row wins on lookup. <c>null</c> when an
    /// operation isn't safe to dedupe (typically read-only calls).
    /// </summary>
    public string? RequestKey { get; set; }

    /// <summary>JSON of the body / arguments we sent to Fly.</summary>
    public string RequestPayload { get; set; } = string.Empty;

    /// <summary>JSON of the response Fly returned. <c>null</c> while still pending or on transport failure.</summary>
    public string? ResponsePayload { get; set; }

    /// <summary>HTTP status code returned by Fly. <c>null</c> if no response was received.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>Lifecycle state — see <see cref="FlyOperationStatus"/>. Persisted as a string.</summary>
    public FlyOperationStatus Status { get; set; }

    /// <summary>
    /// Short machine-readable error code (e.g. <c>"rate_limited"</c>, <c>"not_found"</c>).
    /// Sourced either from the Fly response body or a synthetic value the client
    /// applies for transport-level failures.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Wall-clock latency of the HTTP call in milliseconds. <c>null</c> while pending.</summary>
    public int? LatencyMs { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
