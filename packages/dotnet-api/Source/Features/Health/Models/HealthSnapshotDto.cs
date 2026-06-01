namespace Source.Features.Health.Models;

/// <summary>
/// HTTP-only DTO for <c>GET /api/runtimes/{runtimeId}/health-snapshots</c>.
/// Mirrors the in-memory <see cref="HealthSnapshot"/> shape but is the wire
/// surface — kept separate so internal buffer fields can evolve without
/// touching the API contract.
///
/// <para><b>No <c>[TranspilationSource]</c> attribute.</b> This is HTTP, not
/// SignalR, and the frontend consumes it via Orval-generated React Query
/// hooks (the standard <c>./scripts/generate-swagger.sh</c> path). Tapper
/// only emits typed DTOs for SignalR payloads; the swagger pipeline takes
/// care of HTTP DTOs separately.</para>
/// </summary>
public record HealthSnapshotDto(
    DateTime ReceivedAt,
    double? CpuPct,
    long? MemUsedMb,
    double? DiskUsedPct,
    IReadOnlyList<string> SupervisedServicesUp,
    Guid? ActiveSessionId);
