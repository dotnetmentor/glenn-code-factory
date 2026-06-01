namespace Source.Features.Health;

/// <summary>
/// One row of in-memory daemon telemetry — what the heartbeat handler hands the
/// <see cref="HealthSnapshotBuffer"/> to keep around for the read endpoint.
/// Mirrors the health fields on <c>HeartbeatPayload</c> plus the server-clock
/// timestamp at receive (server clock wins for ordering, same rationale as
/// <c>ProjectRuntime.LastHeartbeatAt</c>).
///
/// <para>Plain record on purpose — the buffer is purely process-local
/// telemetry. There is no <c>[TranspilationSource]</c>, no EF mapping; the
/// HTTP read endpoint maps these into a separate <c>HealthSnapshotDto</c>
/// before serializing so the wire shape stays explicit even if internals
/// shift.</para>
/// </summary>
public record HealthSnapshot(
    DateTime ReceivedAt,
    double? CpuPct,
    long? MemUsedMb,
    double? DiskUsedPct,
    IReadOnlyList<string> SupervisedServicesUp,
    Guid? ActiveSessionId);
