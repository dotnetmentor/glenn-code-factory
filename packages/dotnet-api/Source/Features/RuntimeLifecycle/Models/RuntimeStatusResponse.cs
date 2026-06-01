namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// User-facing status snapshot returned by
/// <c>GET /api/projects/{projectId}/branches/{branchId}/runtime/status</c>. The
/// contract is deliberately narrow — only the fields the runtime drawer needs
/// to render its header + Sysstats panel — so we don't leak operator-only
/// details like Fly volume ids or per-tenant idle thresholds.
///
/// <para><see cref="RecentTransitions"/> is the most-recent five lifecycle moves, newest
/// first, so the UI can show a tiny "what just happened" timeline without a follow-up
/// round trip to the admin detail endpoint.</para>
///
/// <para><see cref="ErrorReason"/> and <see cref="ErrorMessage"/> are populated only when
/// <see cref="State"/> is <see cref="RuntimeState.Failed"/> — surfaced from the most recent
/// lifecycle transition so the UI can show "why" without a follow-up round trip. Both
/// null otherwise. <c>ErrorReason</c> is the structured machine code (e.g.
/// <c>"provisioner:no_active_image"</c>); <c>ErrorMessage</c> is the human-readable
/// metadata that accompanied the transition.</para>
///
/// <para><b>Observability snapshot fields.</b> The five
/// <c>Last*</c> fields below mirror the columns added to
/// <see cref="ProjectRuntime"/> by the observability foundation card.
/// <see cref="RespawnRetries"/> drives the header's "Respawned N×" pill (audit
/// item A11). <see cref="LastDiskUsedBytes"/> / <see cref="LastDiskTotalBytes"/>
/// / <see cref="LastDiskSampledAt"/> back the Sysstats panel disk row.
/// <see cref="LastSysstatsSnapshot"/> / <see cref="LastSupervisordSnapshot"/>
/// are raw jsonb strings the frontend parses against the shapes documented on
/// the daemon side — keeping them strings here lets the daemon evolve its
/// snapshot schema without coordinated C# migrations.</para>
/// </summary>
/// <para><b>Spec-health fields (self-healing-runtime-specs).</b>
/// <see cref="SpecHealth"/> is the runtime's spec-application health, decoupled
/// from <see cref="State"/> — a runtime can be <see cref="RuntimeState.Online"/>
/// while <see cref="SpecHealth"/> is <see cref="RuntimeSpecHealth.Degraded"/>
/// (alive but the spec didn't fully apply). The frontend's amber "Runtime
/// started, but the spec didn't fully apply" banner keys off this. When
/// degraded, <see cref="RecentBootIssues"/> carries the most-recent boot-issue
/// events (InstallFailed / ServiceCrashed / ServiceFailedToStart /
/// SpecDeltaFailed / ServiceEnvMissing / SpecDegraded) so the banner can render
/// an expandable issue list without a follow-up round trip to the events
/// endpoint. Empty for a healthy runtime.</para>
public record RuntimeStatusResponse(
    Guid RuntimeId,
    RuntimeState State,
    DateTime StateChangedAt,
    DateTime? LastHeartbeatAt,
    string? FlyMachineId,
    string? ImageDigest,
    string Region,
    List<RuntimeTransitionDto> RecentTransitions,
    string? ErrorReason = null,
    string? ErrorMessage = null,
    int RespawnRetries = 0,
    long? LastDiskUsedBytes = null,
    long? LastDiskTotalBytes = null,
    DateTime? LastDiskSampledAt = null,
    string? LastSysstatsSnapshot = null,
    string? LastSupervisordSnapshot = null,
    RuntimeSpecHealth SpecHealth = RuntimeSpecHealth.Unknown,
    List<RuntimeBootIssueDto>? RecentBootIssues = null);
