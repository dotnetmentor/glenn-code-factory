namespace Source.Features.RuntimeEvents.Models;

/// <summary>
/// String constants for the <see cref="RuntimeEvent.Type"/> column. Exposed as
/// <c>const string</c> (not an enum) so:
///
/// <list type="bullet">
///   <item>The daemon and backend can both emit the same wire values without
///         a shared C# enum — the daemon is a separate process / repo and
///         consumes these as JSON strings.</item>
///   <item>Adding a new event type is one line; no migration, no value-shift
///         risk on existing rows.</item>
///   <item>Switch statements and pattern matching get exhaustive-style ergonomics
///         (constant case labels) without forcing every consumer to import an
///         enum.</item>
/// </list>
///
/// <para>Taxonomy mirrors the runtime-spec-v2 specification's "Event taxonomy"
/// section. Keep this list in sync with the daemon's emitter constants — the
/// spec is the source of truth, this file is a mirror.</para>
/// </summary>
public static class RuntimeEventTypes
{
    // -------- Bootstrap stages --------
    public const string BootstrapStageStarted = "BootstrapStageStarted";
    public const string BootstrapStageCompleted = "BootstrapStageCompleted";
    public const string BootstrapStageFailed = "BootstrapStageFailed";

    // -------- Install (apt / mise / curl-pipe-bash snippets) --------
    public const string InstallStarted = "InstallStarted";
    public const string InstallCompleted = "InstallCompleted";
    public const string InstallSkipped = "InstallSkipped";
    public const string InstallFailed = "InstallFailed";

    // -------- Per-boot setup commands (npm install, migrations, …) --------
    public const string SetupCommandStarted = "SetupCommandStarted";
    public const string SetupCommandCompleted = "SetupCommandCompleted";
    public const string SetupCommandFailed = "SetupCommandFailed";

    /// <summary>
    /// Batched stdout/stderr lines from install or setup bash, emitted on a
    /// ~2-second flush cadence (or whenever the per-stream buffer reaches 50
    /// lines, whichever comes first). Lets the Timeline tab show live progress
    /// during long-running steps like <c>dotnet restore</c> or
    /// <c>mise install</c> instead of going silent between the
    /// <see cref="InstallStarted"/> / <see cref="SetupCommandStarted"/>
    /// bookends and their matching <c>Completed</c> events. Persisted via the
    /// usual <c>RuntimeEvents</c> pipeline so the operator can scroll back
    /// through what happened after the fact. Payload:
    /// <c>{ stage: "install"|"setup", stream: "stdout"|"stderr",
    /// lines: string[], batchedAt: ISO8601, lineCount: int,
    /// flushReason: "interval"|"size" }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Info"/>.
    /// </summary>
    public const string BootstrapOutputChunk = "BootstrapOutputChunk";

    // -------- Supervised service lifecycle --------
    public const string ServiceStarting = "ServiceStarting";
    public const string ServiceRunning = "ServiceRunning";
    public const string ServiceCrashed = "ServiceCrashed";
    public const string ServiceRestarted = "ServiceRestarted";

    /// <summary>
    /// Pre-flight guard tripped: a service declared one or more
    /// <c>requiredEnv</c> keys (from its <c>ServiceSpec.RequiredEnv</c>) that
    /// are absent or empty in the runtime's env snapshot at start time. The
    /// daemon's <c>StartingServicesStage</c> SKIPS supervisord registration for
    /// that service rather than letting it boot and crash-loop on the missing
    /// variable, then emits this event. The runtime still reaches Online
    /// (degraded) so the operator can add the secret in the Environment tab and
    /// restart the affected service. Payload:
    /// <c>{ serviceName, missingEnvVars: string[] }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/>.
    /// </summary>
    public const string ServiceEnvMissing = "ServiceEnvMissing";

    /// <summary>
    /// Per-service active healthcheck command (the <c>healthcheck.command</c>
    /// declared on a <c>ServiceSpec</c>) returned exit 0 within the stage's
    /// deadline. Emitted by the daemon's <c>StartingServicesStage</c> the first
    /// time a probe succeeds; the inner probe loop then breaks for that
    /// service. Payload: <c>{ serviceName, durationMs, lastExitCode }</c>
    /// where <c>durationMs</c> is measured from when the probe loop started
    /// and <c>lastExitCode</c> is always 0 (kept for symmetry with the
    /// <see cref="ServiceHealthcheckTimedOut"/> payload). Severity:
    /// <see cref="RuntimeEventSeverity.Info"/>.
    /// </summary>
    public const string ServiceHealthy = "ServiceHealthy";

    /// <summary>
    /// Per-service active healthcheck command never returned exit 0 within
    /// the stage's deadline. Emitted by the daemon's
    /// <c>StartingServicesStage</c>; the stage does NOT fail when this is
    /// emitted — the healthcheck is advisory once supervisord reports the
    /// service RUNNING. Payload: <c>{ serviceName, deadlineMs, lastExitCode,
    /// lastStdoutTail, lastStderrTail }</c> where the tails are capped at
    /// ~2KB each (curl / pg_isready outputs are short by nature). Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/> — process-alive is still
    /// required, but the post-RUNNING probe is best-effort.
    /// </summary>
    public const string ServiceHealthcheckTimedOut = "ServiceHealthcheckTimedOut";

    /// <summary>
    /// Per-service active healthcheck command returned non-zero for a single
    /// probe iteration. Bucketed emission: only emitted when the exit code
    /// transitions from the previous probe OR every 5th attempt — without
    /// that throttle a 180s deadline at a 5s interval would emit 36 events
    /// per service. Payload:
    /// <c>{ serviceName, attemptCount, exitCode, stdoutTail, stderrTail,
    /// attemptedAt }</c>. Severity: <see cref="RuntimeEventSeverity.Info"/>
    /// (debug-grade signal; useful for diagnosing flaky probes but not an
    /// operator-actionable alert on its own).
    /// </summary>
    public const string ServiceHealthcheckProbeFailed = "ServiceHealthcheckProbeFailed";

    /// <summary>
    /// Batched stdout/stderr lines from a supervised service captured during
    /// the <c>starting-services</c> bootstrap window (between supervisord
    /// <c>addService</c> returning and the stage emitting its final result).
    /// Same flush cadence as <see cref="BootstrapOutputChunk"/> (~2s or
    /// 50 lines, whichever comes first), but scoped to one service so the
    /// Timeline can label the lines with the source program. Payload:
    /// <c>{ serviceName, stream: "stdout"|"stderr", lines: string[],
    /// batchedAt: ISO8601, lineCount: int, flushReason: "interval"|"size" }</c>.
    /// Severity: <see cref="RuntimeEventSeverity.Info"/>. Tailing stops when
    /// the stage returns — post-Online live-tail is a separate concern handled
    /// by the service-logs SignalR stream.
    /// </summary>
    public const string ServiceOutputChunk = "ServiceOutputChunk";

    // -------- Spec delta application --------
    public const string SpecDeltaApplied = "SpecDeltaApplied";
    public const string SpecDeltaFailed = "SpecDeltaFailed";
    public const string SpecValidationFailed = "SpecValidationFailed";

    /// <summary>
    /// Degraded-online bootstrap (spec <c>self-healing-runtime-specs</c>, card
    /// D1). A NON-CRITICAL (spec) bootstrap stage — Install, RunningSetup, or
    /// StartingServices — failed deterministically, so the daemon's
    /// <c>BootstrapOrchestrator</c> recorded a <c>BootIssue</c> and CONTINUED
    /// instead of aborting the boot. One event is emitted per boot issue just
    /// before <c>ReportReadyStage</c> runs, so the runtime still reaches Online
    /// but in a <c>Degraded</c> <c>SpecHealth</c> state. The amber degraded
    /// banner on the runtime UI and the "let agent fix it" self-heal loop key
    /// off these events. Payload:
    /// <c>{ stage, service?, reason, detail?, occurredAt }</c> (mirrors the
    /// daemon's <c>BootIssue</c> shape). Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/> — the runtime is alive, the
    /// spec just didn't fully apply.
    /// </summary>
    public const string SpecDegraded = "SpecDegraded";

    // -------- Observability foundation (super-admin polish) --------
    //
    // These constants are added by the observability-foundation card so swagger
    // surfaces the type names that daemon + lifecycle-job cards will emit. Each
    // is mirrored in the daemon's emitter constants (or backend Hangfire job)
    // by the dependent cards — this file is the single source of truth for the
    // wire string. See spec `runtime-observability-super-admin` Section B for
    // payload shapes and emission rules.

    /// <summary>
    /// Service supervised by supervisord declared FATAL (exhausted
    /// <c>startretries</c>) without ever reaching RUNNING. Emitted by the
    /// daemon's <c>ServiceStatusPoller</c>. Payload:
    /// <c>{ serviceName, attemptCount, finalState, exitStatus, spawnErr,
    /// stderrTailLines }</c>. Severity: <see cref="RuntimeEventSeverity.Error"/>.
    /// </summary>
    public const string ServiceFailedToStart = "ServiceFailedToStart";

    /// <summary>
    /// Daemon attempted to ack a <c>RuntimeSpecDeltaApplied</c> result but the
    /// SignalR send rejected (typically payload-too-large or transport blow-up).
    /// Emitted by the daemon's <c>RuntimeSpecApplier.#sendAck</c> error path.
    /// Payload: <c>{ proposalId, errorMessage, ackPayloadBytes,
    /// signalrConnectionState }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/>.
    /// </summary>
    public const string SpecApplyAckFailed = "SpecApplyAckFailed";

    /// <summary>
    /// Our DB's view of a runtime's state disagrees with what Fly's machines
    /// API returns. Emitted by the backend's <c>FlyDriftPollerJob</c> on each
    /// 60-second tick where the live machine state doesn't match our stored
    /// runtime state. Payload: <c>{ flyState, ourState, machineId, region }</c>.
    /// Severity: <see cref="RuntimeEventSeverity.Warning"/>.
    /// </summary>
    public const string RuntimeFlyDriftDetected = "RuntimeFlyDriftDetected";

    /// <summary>
    /// Cloudflared tunnel for this runtime stopped responding (health check
    /// HEAD against the tunnel hostname failed). Emitted by the daemon's
    /// <c>CloudflaredController</c> failure-transition path. Payload:
    /// <c>{ hostname, lastError, lastSuccessAt }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/>.
    /// </summary>
    public const string CloudflaredTunnelDown = "CloudflaredTunnelDown";

    /// <summary>
    /// Cloudflared tunnel for this runtime is back up after a prior
    /// <see cref="CloudflaredTunnelDown"/>. Emitted by the daemon's
    /// <c>CloudflaredController</c> recovery-transition path. Payload:
    /// <c>{ hostname, lastError, lastSuccessAt }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Info"/>.
    /// </summary>
    public const string CloudflaredTunnelUp = "CloudflaredTunnelUp";

    /// <summary>
    /// Backend's <c>RespawnRuntimeJob</c> decided to respawn a crashed runtime
    /// (destroy old Fly machine + create replacement). Emitted before the new
    /// boot is triggered. Payload: <c>{ attemptNumber, lastFailureReason,
    /// lastFailureMessage, secondsSinceLastHeartbeat }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Warning"/>.
    /// </summary>
    public const string RuntimeRespawnTriggered = "RuntimeRespawnTriggered";

    /// <summary>
    /// Disk pressure on the runtime's <c>/data</c> volume transitioned to
    /// critical. Emitted by the daemon's <c>DiskMonitor</c> on the
    /// non-critical → critical edge. Payload:
    /// <c>{ usedBytes, totalBytes, level, mountpoint }</c>. Severity:
    /// <see cref="RuntimeEventSeverity.Error"/>.
    /// </summary>
    public const string DiskPressureCritical = "DiskPressureCritical";
}
