namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// One row in the "recent operations" list — a projection of
/// <see cref="Source.Features.FlyManagement.Models.FlyOperation"/> shaped for the
/// operator UI's "what did we ask Fly to do?" timeline.
///
/// <para><see cref="Status"/> is serialised as a string (not the
/// <see cref="Source.Features.FlyManagement.Models.FlyOperationStatus"/> enum) so the
/// frontend sees the human-readable value directly — matches how the rest of the
/// admin DTOs surface enum values.</para>
///
/// <para><see cref="RequestPayload"/> and <see cref="ResponsePayload"/> are inlined
/// verbatim. They're capped to 20 rows by the calling service so the worst-case JSON
/// payload size of this list is bounded even when Fly returns big bodies.</para>
/// </summary>
public sealed class FlyOperationView
{
    /// <summary>DB primary key of the audit row.</summary>
    public Guid Id { get; init; }

    /// <summary>Logical operation name, e.g. <c>"CreateMachine"</c>, <c>"SuspendMachine"</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Status string — see <see cref="Source.Features.FlyManagement.Models.FlyOperationStatus"/>.</summary>
    public required string Status { get; init; }

    /// <summary>HTTP status code Fly returned. Null when the call didn't get that far (transport failure).</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Wall-clock latency of the HTTP call in milliseconds. Null while pending or on transport failure.</summary>
    public int? LatencyMs { get; init; }

    /// <summary>Short error code parsed from Fly's response body, when present.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>UTC timestamp the audit row was created (i.e. when the call was initiated).</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Raw JSON body we sent. <c>"{}"</c> for argumentless calls.</summary>
    public required string RequestPayload { get; init; }

    /// <summary>Raw JSON body Fly returned. Null on pending / transport-failed rows.</summary>
    public string? ResponsePayload { get; init; }
}
