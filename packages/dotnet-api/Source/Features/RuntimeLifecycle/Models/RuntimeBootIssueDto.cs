namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Wire-shape projection of a single boot-issue <c>RuntimeEvent</c> surfaced on
/// <see cref="RuntimeStatusResponse.RecentBootIssues"/> (self-healing-runtime-specs,
/// card B1). The user-facing "Runtime started, but the spec didn't fully apply"
/// banner renders these as its expandable issue list when
/// <see cref="RuntimeStatusResponse.SpecHealth"/> is
/// <see cref="RuntimeSpecHealth.Degraded"/>.
///
/// <para>Sourced from the <c>RuntimeEvents</c> store, filtered to the boot-issue
/// types (InstallFailed, ServiceCrashed, ServiceFailedToStart, SpecDeltaFailed,
/// ServiceEnvMissing, SpecDegraded). Boot-issue <i>details</i> live there — not
/// on the <c>ProjectRuntime</c> row — so this is a read-only projection, not a
/// persisted shape.</para>
///
/// <list type="bullet">
///   <item><see cref="Type"/> is the <c>RuntimeEventTypes</c> string constant.</item>
///   <item><see cref="Severity"/> is the event's severity string
///         ("Info"/"Warning"/"Error") for banner colour-coding.</item>
///   <item><see cref="Timestamp"/> is the daemon's emit clock — what the banner
///         orders / captions on, not the server insert stamp.</item>
///   <item><see cref="Payload"/> is the event's already-serialized JSON payload
///         (e.g. <c>{ stage, service?, reason, detail? }</c>) passed through
///         verbatim; the frontend re-parses against the per-type shape.</item>
/// </list>
/// </summary>
public record RuntimeBootIssueDto(
    string Type,
    string Severity,
    DateTime Timestamp,
    string Payload);
