// RuntimeEventTypes — TypeScript mirror of the C# constants in
// `/workspace/packages/dotnet-api/Source/Features/RuntimeEvents/Models/RuntimeEventTypes.cs`.
//
// Wire values are plain strings (not an enum) so the daemon and backend can
// evolve the taxonomy without a coordinated migration: adding a new constant
// is one line on each side, dropping one is a deprecation + grep on each side.
//
// Source of truth is the "Event taxonomy" section of runtime-spec-v2.md. Keep
// these in lockstep with the C# RuntimeEventTypes class — drift will silently
// degrade the Timeline tab in the runtime drawer (the backend filter/query
// columns key off these exact strings).

export const RuntimeEventTypes = {
  // -------- Bootstrap stages --------
  BootstrapStageStarted: 'BootstrapStageStarted',
  BootstrapStageCompleted: 'BootstrapStageCompleted',
  BootstrapStageFailed: 'BootstrapStageFailed',

  // -------- Install (apt / mise / curl-pipe-bash snippets) --------
  InstallStarted: 'InstallStarted',
  InstallCompleted: 'InstallCompleted',
  InstallSkipped: 'InstallSkipped',
  InstallFailed: 'InstallFailed',

  // -------- Per-boot setup commands (npm install, migrations, …) --------
  SetupCommandStarted: 'SetupCommandStarted',
  SetupCommandCompleted: 'SetupCommandCompleted',
  SetupCommandFailed: 'SetupCommandFailed',

  /**
   * Batched stdout/stderr lines from install or setup bash. The daemon's
   * `BootstrapOutputBatcher` flushes ~every 2 seconds (or whenever the
   * per-stream buffer reaches 50 lines) and emits one event per stream per
   * flush. Lets the Timeline tab show live progress during long-running steps
   * like `dotnet restore` or `mise install`. Mirrors
   * `Source.Features.RuntimeEvents.Models.RuntimeEventTypes.BootstrapOutputChunk`
   * on the backend.
   */
  BootstrapOutputChunk: 'BootstrapOutputChunk',

  // -------- Supervised service lifecycle --------
  ServiceStarting: 'ServiceStarting',
  ServiceRunning: 'ServiceRunning',
  ServiceCrashed: 'ServiceCrashed',
  ServiceRestarted: 'ServiceRestarted',

  /**
   * Pre-flight guard: a service declared one or more `requiredEnv` keys that
   * are absent (or empty) in the runtime's env snapshot at start time. The
   * daemon SKIPS supervisord registration for that service rather than letting
   * it boot and crash-loop on the missing var. Emitted by
   * `StartingServicesStage` before `addService`. Payload:
   * `{ serviceName, missingEnvVars: string[] }`. Severity: Warn (the runtime
   * still comes Online — degraded — so the operator can add the secret and
   * restart the service). Mirrors
   * `Source.Features.RuntimeEvents.Models.RuntimeEventTypes.ServiceEnvMissing`.
   */
  ServiceEnvMissing: 'ServiceEnvMissing',

  /**
   * Per-service active healthcheck (`healthcheck.command` from ServiceSpec)
   * returned exit 0 within the stage deadline. Emitted by
   * `StartingServicesStage` on the first successful probe; the inner probe
   * loop then breaks for that service. Payload:
   * `{ serviceName, durationMs, lastExitCode: 0 }`. Mirrors
   * `Source.Features.RuntimeEvents.Models.RuntimeEventTypes.ServiceHealthy`.
   */
  ServiceHealthy: 'ServiceHealthy',

  /**
   * Per-service active healthcheck never returned exit 0 within the stage
   * deadline. Emitted by `StartingServicesStage` — does NOT fail the stage
   * (process-alive is still required, but the healthcheck is advisory once
   * the service is RUNNING). Payload:
   * `{ serviceName, deadlineMs, lastExitCode, lastStdoutTail, lastStderrTail }`.
   * Severity: Warn.
   */
  ServiceHealthcheckTimedOut: 'ServiceHealthcheckTimedOut',

  /**
   * Single healthcheck probe iteration returned non-zero. Bucketed emission
   * (only on exit-code transition OR every 5th attempt) so a 180s × 5s loop
   * doesn't flood the events table. Payload:
   * `{ serviceName, attemptCount, exitCode, stdoutTail, stderrTail,
   * attemptedAt }`. Severity: Info.
   */
  ServiceHealthcheckProbeFailed: 'ServiceHealthcheckProbeFailed',

  /**
   * Batched stdout/stderr lines from a supervised service tailed during the
   * `starting-services` bootstrap window. Same flush cadence as
   * `BootstrapOutputChunk` (~2s or 50 lines). Payload:
   * `{ serviceName, stream, lines, batchedAt, lineCount, flushReason }`.
   * Mirrors `RuntimeEventTypes.ServiceOutputChunk` on the backend.
   */
  ServiceOutputChunk: 'ServiceOutputChunk',

  // -------- Spec delta application --------
  SpecDeltaApplied: 'SpecDeltaApplied',
  SpecDeltaFailed: 'SpecDeltaFailed',
  SpecValidationFailed: 'SpecValidationFailed',

  /**
   * Degraded-online bootstrap (self-healing-runtime-specs, card D1). A
   * NON-CRITICAL (spec) stage — Install / RunningSetup / StartingServices —
   * failed deterministically, so the orchestrator recorded a `BootIssue` and
   * CONTINUED rather than aborting the boot. One event is emitted per boot
   * issue just before `ReportReadyStage` runs, so the runtime still reaches
   * Online but in a `Degraded` SpecHealth state. The amber UI banner + the
   * agent self-heal loop key off these. Payload:
   * `{ stage, service?, reason, detail?, occurredAt }` (mirrors the BootIssue
   * shape). Severity: Warn — the runtime is alive, the spec just didn't fully
   * apply. Mirrors
   * `Source.Features.RuntimeEvents.Models.RuntimeEventTypes.SpecDegraded`.
   */
  SpecDegraded: 'SpecDegraded',

  // -------- Observability foundation (super-admin polish) --------
  // Mirror of the C# constants under `Source.Features.RuntimeEvents.Models.RuntimeEventTypes`
  // added by the backend's observability-foundation card. Keep in lockstep.

  /** Service declared FATAL by supervisord without ever reaching RUNNING. */
  ServiceFailedToStart: 'ServiceFailedToStart',
  /** Daemon's RuntimeSpecDeltaApplied ack rejected by SignalR (payload-too-large, transport blow-up). */
  SpecApplyAckFailed: 'SpecApplyAckFailed',
  /** Backend-emitted: our runtime view disagrees with Fly's live machine view. */
  RuntimeFlyDriftDetected: 'RuntimeFlyDriftDetected',
  /** Cloudflared tunnel hostname health check transitioned Up → Down. */
  CloudflaredTunnelDown: 'CloudflaredTunnelDown',
  /** Cloudflared tunnel hostname health check transitioned Down → Up after a prior Down. */
  CloudflaredTunnelUp: 'CloudflaredTunnelUp',
  /** Backend-emitted: RespawnRuntimeJob decided to respawn a crashed runtime. */
  RuntimeRespawnTriggered: 'RuntimeRespawnTriggered',
  /** Disk pressure on `/data` transitioned to critical. */
  DiskPressureCritical: 'DiskPressureCritical',
} as const

export type RuntimeEventType =
  (typeof RuntimeEventTypes)[keyof typeof RuntimeEventTypes]
