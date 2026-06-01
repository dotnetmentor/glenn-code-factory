// main.ts — composition root for the daemon process.
//
// Scene 1 of the daemon-architecture spec runs verbatim here:
//
//   1. DaemonConfig.fromEnv()                      — fail → exit(1)
//   2. Build pino logger
//   3. Build SignalRClient + signalr.start()        — fail → exit(1)
//   4. Build BootstrapOrchestrator with stub stages
//   5. Build DiskMonitor, TurnRunner, HeartbeatModule, QuietModeManager,
//      ShutdownCoordinator (deps wired)
//   6. shutdownCoordinator.install({ cancelInFlightBootstrap })
//      ── BEFORE any I/O after this point so a fast SIGTERM mid-bootstrap
//         unwinds cleanly via the AbortSignal.
//   7. turnRunner.start()                           — wires StartTurn /
//      CancelTurn handlers BEFORE bootstrap. ReportReadyStage's
//      `signalr.runtimeReady()` causes the server to fan StartTurn back on
//      the same socket synchronously, so the handler MUST exist by then —
//      otherwise queued sessions get dropped on the daemon floor.
//   8. bootstrap.start(abortSignal)                 — fail → emit + exit(1)
//   9. Wire remaining SignalR inbound handlers:
//        UpdateConfig    → applyConfigUpdate (token rotation v1)
//        RestartService  → restart_service tool path
//   9. heartbeat.start(), disk.start(), quietMode.start()
//   10. Log "daemon ready"
//
// === Why factory functions for the deps ===
//
// Production wires concrete classes; tests want to inject fakes for every
// module. Rather than scatter `new Foo(...)` calls through `runMain`, we name
// every construction step as a factory on `MainDeps` and let `runMain` call
// `deps.buildFoo(...)`. Each factory takes only the prior-stage deps it needs
// (e.g. `buildSignalR(config, logger)`) so the test can override one factory
// without rebuilding the rest of the graph.
//
// `MainDeps` is `Partial`-overridable; the runtime fills in the defaults. The
// CLI entry point passes nothing.
//
// === Runtime-scope events stopgap ===
//
// Several events emitted from this file (`bootstrap_failed`,
// `protocol_version_mismatch`) need a runtime-scope channel that doesn't
// exist on the .NET hub yet. We mirror BootstrapOrchestrator and
// ShutdownCoordinator: send via `signalr.emitEvent` with `sessionId: ''`,
// `eventType: 'AssistantText'` as a generic carrier, real type embedded in
// `eventData` JSON. Replace once runtime-bootstrap spec adds a real
// runtime-scope hub method.

import { spawn } from 'node:child_process'
import { access, mkdir, readdir, readFile, rename, rm, unlink, writeFile } from 'node:fs/promises'
// NOTE: do NOT add `import { createRequire } from 'node:module'` here.
// esbuild's ESM-to-CJS interop banner already injects
//   `import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);`
// at the top of the bundle, so a second top-level `createRequire` import in
// the source produces `SyntaxError: Identifier 'createRequire' has already
// been declared` at boot. The banner's `require` is exactly what we'd build
// with `createRequire(import.meta.url)` anyway — use it directly below.
import os from 'node:os'
import path from 'node:path'

import pino, { type Logger } from 'pino'

import { AttachmentStager } from './attachments/AttachmentStager.js'
import { BootRetryCounter } from './bootstrap/BootRetryCounter.js'
import {
  BootstrapAbortedError,
  BootstrapOrchestrator,
  type BootstrapStage,
} from './bootstrap/BootstrapOrchestrator.js'
import { BootstrapState } from './bootstrap/BootstrapState.js'
import { CloningRepoStage } from './bootstrap/stages/CloningRepoStage.js'
import { ConnectingStage } from './bootstrap/stages/ConnectingStage.js'
import { FetchingStage } from './bootstrap/stages/FetchingStage.js'
import { BootIssueStore } from './bootstrap/BootIssueStore.js'
import type { BootIssue } from './bootstrap/BootIssueStore.js'
import { InstallStage } from './bootstrap/stages/InstallStage.js'
import { ReportReadyStage } from './bootstrap/stages/ReportReadyStage.js'
import { RunningSetupStage } from './bootstrap/stages/RunningSetupStage.js'
import { StartingServicesStage } from './bootstrap/stages/StartingServicesStage.js'
import { VerifyEnvStage } from './bootstrap/stages/VerifyEnvStage.js'
import { WritingConfigStage } from './bootstrap/stages/WritingConfigStage.js'
import { DaemonConfig } from './config/DaemonConfig.js'
import { DiskMonitor } from './disk/DiskMonitor.js'
import { EnvVarManager } from './env/EnvVarManager.js'
import {
  DefaultRuntimeEventEmitter,
  type RuntimeEventEmitter,
} from './events/RuntimeEventEmitter.js'
import { RuntimeEventTypes } from './events/RuntimeEventTypes.js'
import { createTokenManager, type TokenManager } from './github/TokenManager.js'
import { parseRepoFullName } from './github/repoFullName.js'
import { McpRegistry } from './mcp/McpRegistry.js'
import { buildCommitMessage } from './git/CommitMessageGenerator.js'
import { DestructiveOpGate } from './git/DestructiveOpGate.js'
import { GitModule } from './git/GitModule.js'
import { GitRunner } from './git/GitRunner.js'
import { PushRetryJob } from './git/PushRetryJob.js'
import { SshKeyHandler } from './git/SshKeyHandler.js'
import type { GitAuditEvent } from './git/types.js'
import { HeartbeatModule } from './heartbeat/HeartbeatModule.js'
import { LivenessWorker } from './heartbeat/LivenessWorker.js'
import { SelfWatchdog } from './heartbeat/SelfWatchdog.js'
import { FileChangeWatcher } from './hooks/FileChangeWatcher.js'
import { HookEventEmitter } from './hooks/HookEventEmitter.js'
import { HookExecutor } from './hooks/HookExecutor.js'
import {
  HOOK_CONFIG_EMPTY,
  HooksModule,
  type HookConfig,
  type HookLifecycleEvent,
} from './hooks/HooksModule.js'
import { SelfHealCoordinator } from './hooks/SelfHealCoordinator.js'
import { ShutdownCoordinator } from './lifecycle/ShutdownCoordinator.js'
import { LogTailer } from './logs/LogTailer.js'
import { pinoSecretRedactionOptions } from './logging/secretRedactor.js'
import { ChildProcessExecutor } from './runtime/ChildProcessExecutor.js'
import { CloudflaredController } from './runtime/CloudflaredController.js'
import type { IExecutor } from './runtime/IExecutor.js'
import { InstallHashStore } from './runtime/InstallHashStore.js'
import { RuntimeSpecApplier } from './runtime/RuntimeSpecApplier.js'
import { ServiceStatusPoller } from './runtime/ServiceStatusPoller.js'
import { SupervisordController } from './runtime/SupervisordController.js'
import { SupervisordXmlRpcClient } from './supervisord/SupervisordXmlRpcClient.js'
import { ProcessStatsCollector } from './sysstats/ProcessStatsCollector.js'
import { SignalRClient } from './signalr/SignalRClient.js'
import type {
  ApplyRuntimeSpecDeltaPayload,
  ConfigUpdatePayload,
  EmitEventPayload,
  ExecuteDestructiveGitOpPayload,
  ForceRebootstrapPayload,
  HeartbeatPayload,
  MergeBranchPayload,
  RestartServicePayload,
} from './signalr/types.js'
import { AgentEventKind } from './signalr/types.js'
import { buildCustomTools } from './tools/CustomTools.js'
import {
  fetchToolDescription,
  type ToolDescriptionResponse,
} from './tools/fetchToolDescription.js'
import { buildCachedGitBranchResolver } from './turn/gitBranchResolver.js'
import { buildCursorFactory } from './turn/CursorFactory.js'
import { QuietModeManager } from './turn/QuietModeManager.js'
import { TurnRunner } from './turn/TurnRunner.js'
import type { CustomTool } from './turn/types.js'
import {
  DaemonToolsMcpServer,
} from './mcp/DaemonToolsMcpServer.js'
import { isBenignAbortRejection } from './utils/isBenignAbortRejection.js'
import { DAEMON_VERSION } from './version.js'

// ============================================================================
// Dependency-injection shape
// ============================================================================
//
// Every module is built via a named factory so a test can replace one (or
// all) with a fake. Factories take the earlier deps they need; the call order
// in `runMain` constructs them in dependency order.
//
// Tests override these by passing `{ buildSignalR: () => fakeSignalR, … }` to
// `runMain`. Production wiring uses the defaults below.

// === Crash reporter refs ===
//
// Set by `runMain` after SignalR is up. The CLI-entry-block crash handlers
// (uncaughtException / unhandledRejection) read these to ship a structured
// RuntimeErrorReports row via `signalr.sendErrorReport` before the process
// dies. NOT touched by tests — fake graphs never assign these, so the handlers
// fall through to the stderr-only path.
let crashReporterSignalR: SignalRClient | undefined
let crashReporterLogger: Logger | undefined

export interface MainDeps {
  loadConfig: () => DaemonConfig
  buildLogger: (config: DaemonConfig) => Logger
  buildSignalR: (config: DaemonConfig, logger: Logger) => SignalRClient
  buildBootstrap: (
    config: DaemonConfig,
    signalr: SignalRClient,
    envVarManager: EnvVarManager,
    mcpRegistry: McpRegistry,
    tokenManager: TokenManager,
    bootstrapState: BootstrapState,
    logger: Logger,
    emitter: RuntimeEventEmitter,
    bootRetryCounter: BootRetryCounter,
    // Shared in-memory boot-issue collector (self-healing-runtime-specs, D1).
    // The same instance is threaded into both the orchestrator (writer) and —
    // by card D2 — the `get_boot_issues` MCP tool (reader).
    bootIssues: BootIssueStore,
  ) => BootstrapOrchestrator
  buildDiskMonitor: (logger: Logger) => DiskMonitor
  /**
   * Fetch the `propose_runtime_spec` tool description + JSON schema from the
   * backend at startup. Wired through MainDeps so tests can stub it (the
   * defaults fetch from `mainApiUrl`; tests pass a constant). See
   * `tools/fetchToolDescription.ts` for the fallback semantics.
   */
  fetchToolDescription: (
    config: DaemonConfig,
    logger: Logger,
  ) => Promise<ToolDescriptionResponse>
  buildTurnRunner: (
    config: DaemonConfig,
    signalr: SignalRClient,
    mcpRegistry: McpRegistry,
    daemonToolsMcpServer: DaemonToolsMcpServer,
    customTools: readonly CustomTool[],
    logger: Logger,
  ) => TurnRunner
  buildDaemonToolsMcpServer: (
    config: DaemonConfig,
    customTools: readonly CustomTool[],
    logger: Logger,
  ) => DaemonToolsMcpServer
  buildHeartbeat: (
    config: DaemonConfig,
    signalr: SignalRClient,
    turnRunner: TurnRunner,
    disk: DiskMonitor,
    quiet: QuietModeManager,
    logger: Logger,
    processStats: ProcessStatsCollector,
  ) => HeartbeatModule
  /**
   * Last-resort liveness guard (Fix #3 / `heartbeat/SelfWatchdog.ts`). Spawns
   * a worker_thread that watches a SharedArrayBuffer timestamp; if the main
   * thread stops updating for > 25 s the worker SIGKILL's the process so
   * entrypoint.sh restarts the daemon before the master flags us Crashed.
   *
   * The factory pattern matches every other module here so tests can swap in
   * a no-op stub — production tests would otherwise leak real worker_threads
   * workers (one per test) and risk a stalled vitest spawn tripping the
   * watchdog mid-suite.
   */
  buildSelfWatchdog: (logger: Logger) => SelfWatchdog
  /**
   * Worker-thread liveness pinger (Fix #1 / `heartbeat/LivenessWorker.ts`).
   * Spawns a worker that fetches `POST /api/runtimes/{id}/heartbeat-tick`
   * every `config.heartbeatIntervalMs` independently of the main event
   * loop. Even when the main thread is starved by a heavy SDK turn (the
   * root cause of the 2026-05-24 runtime-unavailable incident), this
   * worker keeps the master's view of the runtime as "alive" — so the
   * HeartbeatWatcherJob never flips us to Crashed.
   *
   * Wired AFTER signalr is built (we need a stable mainApiUrl + the
   * current runtime token), and started AFTER bootstrap so the worker's
   * first beat doesn't race against a runtime row that the master might
   * not yet have promoted to Online.
   */
  buildLivenessWorker: (config: DaemonConfig, logger: Logger) => LivenessWorker
  buildQuietMode: (config: DaemonConfig, turnRunner: TurnRunner, logger: Logger) => QuietModeManager
  buildShutdownCoordinator: (
    config: DaemonConfig,
    turnRunner: TurnRunner,
    heartbeat: HeartbeatModule,
    livenessWorker: LivenessWorker,
    disk: DiskMonitor,
    quiet: QuietModeManager,
    signalr: SignalRClient,
    logger: Logger,
    fileWatcher: FileChangeWatcher,
    pushRetryJob: PushRetryJob,
    destructiveOpGate: DestructiveOpGate,
    logTailer: LogTailer,
  ) => ShutdownCoordinator
  buildCustomTools: (
    config: DaemonConfig,
    logger: Logger,
    proposeRuntimeSpec: ToolDescriptionResponse,
    // Self-heal callbacks (self-healing-runtime-specs, card D2). Bind the
    // read-only diagnostic tools + the rebootstrap escape hatch to live daemon
    // state. Optional so test graphs that don't exercise self-heal can omit
    // them; production always supplies all three.
    selfHeal?: {
      getRuntimeSpec: () => { version: string; runtimeSpec: unknown } | null
      listBootIssues: () => BootIssue[]
      triggerRebootstrap: (reason: string) => void | Promise<void>
    },
  ) => readonly CustomTool[]
  // Hook subsystem (Card 10 of daemon-hooks-runner). Each factory takes only
  // the prior-stage deps it needs; tests can swap any one without rebuilding
  // the whole chain.
  buildHookExecutor: (config: DaemonConfig, logger: Logger) => HookExecutor
  buildHooksModule: (executor: HookExecutor, logger: Logger) => HooksModule
  buildFileChangeWatcher: (logger: Logger) => FileChangeWatcher
  buildHookEventEmitter: (
    signalr: SignalRClient,
    runtimeId: string,
    logger: Logger,
  ) => HookEventEmitter
  buildSelfHealCoordinator: (
    signalr: SignalRClient,
    emitter: HookEventEmitter,
    runtimeId: string,
    logger: Logger,
  ) => SelfHealCoordinator
  // Git subsystem (Card 10 of daemon-git-ops). The runner's onAudit callback
  // is wired up post-construction (gitModule references runner; runner needs
  // gitModule to forward audit) — we follow the lazy-closure pattern below.
  buildSshKeyHandler: (logger: Logger) => SshKeyHandler
  buildGitRunner: (
    cwd: string,
    config: DaemonConfig,
    logger: Logger,
    onAudit: (e: GitAuditEvent) => void,
  ) => GitRunner
  buildGitModule: (
    runner: GitRunner,
    signalr: SignalRClient,
    tokenManager: TokenManager,
    getRepoFullName: () => string | null,
    logger: Logger,
  ) => GitModule
  buildPushRetryJob: (
    config: DaemonConfig,
    gitModule: GitModule,
    signalr: SignalRClient,
    quietMode: QuietModeManager,
    logger: Logger,
  ) => PushRetryJob
  buildDestructiveOpGate: (
    gitModule: GitModule,
    signalr: SignalRClient,
    logger: Logger,
  ) => DestructiveOpGate
  // Env-var snapshot manager (Spec 14 Card 7). Receives deltas via UpdateConfig
  // and rewrites `<envFilePath>` atomically.
  buildEnvVarManager: (config: DaemonConfig, logger: Logger) => EnvVarManager
  // MCP registry (Spec 15 Card 5). Populated once at boot by
  // WritingConfigStage and read on every turn by CursorFactory to inject
  // project-scoped HTTP MCP servers into the Cursor SDK options.
  buildMcpRegistry: () => McpRegistry
  // GitHub installation-token manager (daemon-github-clone). Caches +
  // single-flights `IRuntimeHub.GetRepoAccessToken` so CloningRepoStage (and
  // any future stage that needs HTTPS auth for the project repo) can ask for
  // a token without hammering the hub. Constructed after the SignalR client
  // is built because it depends on `getRepoAccessToken`.
  buildTokenManager: (signalr: SignalRClient, logger: Logger) => TokenManager
  // Runtime-curation live mutation (Spec 16 Card 6). The executor backs
  // SupervisordController's sudo / child-process calls. Tests inject a fake
  // executor.
  buildExecutor: () => IExecutor
  buildSupervisordController: (executor: IExecutor, logger: Logger) => SupervisordController
  buildRuntimeSpecApplier: (
    signalr: SignalRClient,
    supervisord: SupervisordController,
    executor: IExecutor,
    logger: Logger,
    hashStore: InstallHashStore,
    emitter: RuntimeEventEmitter,
  ) => RuntimeSpecApplier
  /**
   * Build the singleton `RuntimeEventEmitter`. Wired AFTER signalr connects so
   * the emitter can hook onto reconnect for buffer drain. Tests inject a
   * `TestRuntimeEventEmitter` recorder.
   */
  buildRuntimeEventEmitter: (
    signalr: SignalRClient,
    logger: Logger,
  ) => RuntimeEventEmitter
  /**
   * Build the on-demand `LogTailer` (runtime-spec-v2 Phase 5). One instance
   * per daemon; the SignalR `StartLogTail` / `StopLogTail` handlers route
   * into it. The tailer's `onLine` sink is wired to
   * `signalr.sendServiceLogLine` so every line read off a tail goes
   * straight to the hub. Tests inject a recorder.
   */
  buildLogTailer: (signalr: SignalRClient, logger: Logger) => LogTailer
  /**
   * Build the long-lived `ServiceStatusPoller`. Started AFTER bootstrap so it
   * doesn't double-fire ServiceRestarted while StartingServicesStage is still
   * driving services from non-RUNNING into RUNNING.
   */
  buildServiceStatusPoller: (
    supervisord: SupervisordXmlRpcClient,
    emitter: RuntimeEventEmitter,
    signalr: SignalRClient,
    logger: Logger,
  ) => ServiceStatusPoller
  /**
   * Override `process.exit` for tests so they don't tear down the test
   * runner. Production wires `process.exit`.
   */
  exit: (code: number) => void
}

// ============================================================================
// Default factories — production wiring
// ============================================================================

const DISK_MONITOR_PATH = '/data' as const

// Hooks run with the same cwd as TurnRunner: the project repo mount point.
// Keep this constant here (not imported from TurnRunner) so the hook subsystem
// has a clearly-attributed home — the spec ties hooks and turns to the same
// confined working directory but they are separate concerns.
const HOOK_CWD = '/data/project/repo' as const

// Git ops also run from the project repo mount point. Same constant as
// HOOK_CWD by design: hooks and git operate against the same working tree.
const GIT_CWD = '/data/project/repo' as const

const defaultMainDeps: MainDeps = {
  loadConfig: () => DaemonConfig.fromEnv(),

  buildLogger: (config) => buildPinoLogger(config),

  buildSignalR: (config, logger) => new SignalRClient({ config, logger }),

  buildBootstrap: (
    config,
    signalr,
    envVarManager,
    mcpRegistry,
    tokenManager,
    state,
    logger,
    emitter,
    bootRetryCounter,
    bootIssues,
  ) => {
    // Stage order — the bootstrap state machine from runtime-bootstrap Phase C.
    //
    //   1. ConnectingStage         — confirm signalr handshake before issuing
    //                                  hub invokes. main.ts already started the
    //                                  client; this stage waits for Connected.
    //   2. VerifyEnvStage          — wait for /data/project to mount.
    //   3. FetchingStage           — `signalr.invoke('GetBootstrap')` returns
    //                                  the full BootstrapPayloadV2; stashed in
    //                                  shared state for downstream stages.
    //   4. WritingConfigStage      — atomically writes /data/.glenn/{env,
    //                                  hooks.json,mcp.json} and seeds the
    //                                  in-memory EnvVarManager + McpRegistry.
    //   5. InstallStage            — composes top-level + per-service `install`
    //                                  bash from runtimeSpec into one blob,
    //                                  hash-skips on match against
    //                                  /data/.glenn/install-hashes.json, else
    //                                  runs `bash -c` and persists fresh
    //                                  per-scope hashes on success. This is
    //                                  where mise toolchains land + where
    //                                  per-service binaries (redis, minio,
    //                                  mongodb, ...) get apt-installed.
    //   6. CloningRepoStage        — `git clone` (or fetch+reset) from the
    //                                  payload's repo. Skipped when repo=null.
    //   7. RunningSetupStage       — runs payload.runtimeSpec.setup (V2 bash
    //                                  string) via `bash -c` with
    //                                  cwd=/data/project/repo + a fixed PATH
    //                                  that includes /data/mise/shims.
    //   8. StartingServicesStage   — for each `ServiceSpec` in
    //                                  payload.runtimeSpec.services: render +
    //                                  start via supervisord, poll status
    //                                  until RUNNING, then run any
    //                                  per-service healthcheck command.
    //   9. ReportReadyStage        — final `signalr.runtimeReady()` so the
    //                                  backend can flip the runtime to Ready.
    //
    // The `state` carrier is constructed by the caller (runMain) so the same
    // instance can be threaded into GitModule's `getRepoFullName` closure —
    // we read the post-FetchingStage payload from it on every push to derive
    // the `owner/repo` for the GitHub installation-token mint. Hoisting was
    // forced by daemon-auto-commit-push: previously this lived inside the
    // factory and was invisible to the GitModule wiring point.
    const executor = new ChildProcessExecutor()
    // confDir override: see the buildSupervisordController factory below for
    // the rationale — supervisord.conf's [include] glob points at
    // /data/.glenn/supervisor.d, NOT the default /etc/supervisor/conf.d.
    // This is the bootstrap-time controller (used by StartingServicesStage);
    // both code paths must agree on the directory.
    const supervisordController = new SupervisordController({
      executor,
      fs: { readFile, writeFile, access, readdir, unlink },
      logger,
      confDir: '/data/.glenn/supervisor.d',
    })
    const installHashStore = new InstallHashStore()
    const stages: readonly BootstrapStage[] = [
      new ConnectingStage({ signalr }),
      new VerifyEnvStage(),
      new FetchingStage({ signalr, state }),
      new WritingConfigStage({
        signalr,
        state,
        envVarManager,
        mcpRegistry,
        fs: { writeFile, rename, mkdir },
      }),
      new InstallStage({
        signalr,
        state,
        executor,
        hashStore: installHashStore,
        emitter,
      }),
      new CloningRepoStage({
        signalr,
        state,
        executor,
        fs: { mkdir, access, rm },
        tokenManager,
        emitter,
      }),
      new RunningSetupStage({ signalr, state, executor, emitter }),
      new StartingServicesStage({
        signalr,
        state,
        supervisord: supervisordController,
        executor,
        emitter,
        envVarManager,
        // Per-service degraded recording (card D1): a wedged service becomes a
        // BootIssue instead of failing the whole boot.
        bootIssues,
      }),
      new ReportReadyStage(),
    ]
    return new BootstrapOrchestrator({
      stages,
      signalr,
      config,
      logger,
      emitter,
      bootIssues,
      bootAttemptNumber: bootRetryCounter.current,
      // On bootstrap success, reset the persistent counter so the next cold
      // boot starts at attempt 1. Best-effort; failures are caught + logged
      // inside BootRetryCounter — never propagate up here.
      onBootstrapSucceeded: () => bootRetryCounter.reset(),
    })
  },

  buildDiskMonitor: (logger) => new DiskMonitor({ path: DISK_MONITOR_PATH, logger }),

  fetchToolDescription: (config, logger) =>
    fetchToolDescription(config.mainApiUrl.toString(), logger),

  buildTurnRunner: (
    config,
    signalr,
    mcpRegistry,
    daemonToolsMcpServer,
    customTools,
    logger,
  ) => {
    const cursorFactory = buildCursorFactory({
      logger,
      mcpRegistry,
      getRuntimeToken: () => config.runtimeToken,
      getGitBranch: buildCachedGitBranchResolver({ cwd: GIT_CWD }),
      projectRepoDir: GIT_CWD,
      defaultModel: { id: 'auto' },
    })

    return new TurnRunner({
      signalr,
      config,
      cursorFactory,
      customTools,
      daemonToolsMcpServer,
      logger,
    })
  },

  buildDaemonToolsMcpServer: (_config, customTools, logger) => {
    return new DaemonToolsMcpServer({ tools: customTools, logger })
  },

  buildHeartbeat: (config, signalr, turnRunner, disk, quiet, logger, processStats) => {
    // The gather() callback is what HeartbeatModule calls every tick to
    // assemble the payload. Each contributor module exposes its current
    // state via a method we read here — keeps Heartbeat ignorant of TurnRunner
    // / Disk / Quiet shapes.
    const gather = (): HeartbeatPayload => {
      // Phase D — diskUsedPct + activeSessionId now flow on the wire. The
      // SupervisedServicesUp field is still left as `null` (vs an empty
      // array): the supervisord-status enumeration ships in a follow-up card
      // and the .NET service-down detector treats `null` as "this daemon
      // doesn't yet report services" so we don't false-alarm older runtimes.
      // void quiet.isQuiet() left intact as a placeholder until quiet-mode
      // surfaces on the wire (TODO(daemon-architecture)).
      void quiet.isQuiet()

      const turnState = turnRunner.state()
      const diskSample = disk.latest()
      const diskUsedPct =
        diskSample && diskSample.totalBytes > 0
          ? (diskSample.usedBytes / diskSample.totalBytes) * 100
          : null

      const mem = process.memoryUsage()

      // runtime-observability-super-admin — attach the latest DiskMonitor
      // sample (separate from the flat diskUsedPct scalar so the backend can
      // store usedBytes/totalBytes/sampledAt on ProjectRuntime) and the
      // ProcessStatsCollector snapshot (top-N RSS / CPU% + network rates),
      // JSON-stringified so the .NET side can land it verbatim in
      // ProjectRuntime.LastSysstatsSnapshot jsonb.
      const sysstats = processStats.latest()
      let sysstatsSnapshotJson: string | null = null
      if (sysstats !== null) {
        try {
          sysstatsSnapshotJson = JSON.stringify(sysstats)
        } catch (err) {
          logger.warn({ err }, 'failed to serialise sysstats snapshot; dropping for this beat')
        }
      }
      const disk_ = diskSample
        ? {
            usedBytes: diskSample.usedBytes,
            totalBytes: diskSample.totalBytes,
            sampledAt: diskSample.sampledAt.toISOString(),
          }
        : null

      return {
        emittedAt: new Date().toISOString(),
        daemonVersion: config.daemonVersion,
        cpuPercent: null,
        memoryUsedMb: Math.round(mem.rss / (1024 * 1024)),
        diskUsedPct,
        supervisedServicesUp: null,
        activeSessionId: turnState.kind === 'running' ? turnState.sessionId : null,
        disk: disk_,
        sysstatsSnapshotJson,
      }
    }
    return new HeartbeatModule({ signalr, config, gather, logger })
  },

  // Self-watchdog defaults: 25s stall threshold (5s under HeartbeatWatcher's
  // new 30s master threshold so we self-respawn first), 500ms main-thread
  // updates, 1s worker checks, 2s SIGKILL grace. See
  // `heartbeat/SelfWatchdog.ts` for the full rationale.
  buildSelfWatchdog: (logger) => new SelfWatchdog({ logger }),

  // Liveness worker (Fix #1 / `heartbeat/LivenessWorker.ts`). Uses the same
  // beat interval as HeartbeatModule so the two paths land at roughly the
  // same cadence — the master sees a steady tick stream whether main is
  // busy or not. masterUrl is config.mainApiUrl (the same URL signalr
  // already uses for the hub negotiate), runtimeId is the daemon's own id,
  // initialToken is the boot-time JWT. Token rotation is forwarded by the
  // UpdateConfig handler in runMain.
  buildLivenessWorker: (config, logger) =>
    new LivenessWorker({
      logger,
      masterUrl: config.mainApiUrl.toString(),
      runtimeId: config.runtimeId,
      initialToken: config.runtimeToken,
      intervalMs: config.heartbeatIntervalMs,
    }),

  buildQuietMode: (config, turnRunner, logger) =>
    new QuietModeManager({ turnRunner, config, logger }),

  buildShutdownCoordinator: (
    config,
    turnRunner,
    heartbeat,
    livenessWorker,
    disk,
    quiet,
    signalr,
    logger,
    fileWatcher,
    pushRetryJob,
    destructiveOpGate,
    logTailer,
  ) =>
    new ShutdownCoordinator({
      turnRunner,
      heartbeat,
      livenessWorker,
      diskMonitor: disk,
      quietMode: quiet,
      fileWatcher,
      pushRetryJob,
      destructiveOpGate,
      logTailer,
      signalr,
      config,
      logger,
    }),

  buildCustomTools: (config, logger, proposeRuntimeSpec, selfHeal) =>
    buildCustomTools({
      config,
      logger,
      proposeRuntimeSpec,
      approveRestart: async () => ({ approved: true as const }),
      ...(selfHeal !== undefined
        ? {
            getRuntimeSpec: selfHeal.getRuntimeSpec,
            listBootIssues: selfHeal.listBootIssues,
            triggerRebootstrap: selfHeal.triggerRebootstrap,
          }
        : {}),
    }),

  // Hook subsystem factories — Card 10 of daemon-hooks-runner. SIGTERM →
  // SIGKILL escalation grace is threaded from DaemonConfig (Spec 13 Card 10).
  buildHookExecutor: (config, logger) =>
    new HookExecutor({
      cwd: HOOK_CWD,
      env: process.env,
      logger,
      killEscalationMs: config.processKillEscalationMs,
    }),

  buildHooksModule: (executor, logger) => new HooksModule({ executor, logger }),

  buildFileChangeWatcher: (logger) =>
    new FileChangeWatcher({ rootDir: HOOK_CWD, logger }),

  buildHookEventEmitter: (signalr, runtimeId, logger) =>
    new HookEventEmitter({ signalr, runtimeId, logger }),

  buildSelfHealCoordinator: (signalr, emitter, runtimeId, logger) =>
    new SelfHealCoordinator({ signalr, emitter, runtimeId, logger }),

  // Git subsystem factories — Card 10 of daemon-git-ops.
  buildSshKeyHandler: (logger) =>
    new SshKeyHandler({ homeDir: os.homedir(), logger }),

  buildGitRunner: (cwd, config, logger, onAudit) =>
    new GitRunner({
      cwd,
      logger,
      onAudit,
      killEscalationMs: config.processKillEscalationMs,
    }),

  buildGitModule: (runner, signalr, tokenManager, getRepoFullName, logger) =>
    new GitModule({
      runner,
      signalr,
      logger,
      cwd: GIT_CWD,
      tokenManager,
      getRepoFullName,
      autoCommit: false,
    }),

  buildPushRetryJob: (config, gitModule, signalr, quietMode, logger) =>
    new PushRetryJob({
      gitModule,
      signalr,
      quietMode,
      logger,
      intervalMs: config.pushRetryIntervalMs,
      quietIntervalMs: config.pushRetryQuietIntervalMs,
    }),

  buildDestructiveOpGate: (gitModule, signalr, logger) =>
    new DestructiveOpGate({ gitModule, signalr, logger }),

  // Env-var manager (Spec 14 Card 7). envFilePath is taken from DaemonConfig
  // so operators can override the default `/data/.glenn/env` via env.
  buildEnvVarManager: (config, logger) =>
    new EnvVarManager({ envFilePath: config.envFilePath, logger }),

  // MCP registry (Spec 15 Card 5). Stateless until WritingConfigStage calls
  // loadInitial; no constructor deps to thread.
  buildMcpRegistry: () => new McpRegistry(),

  // GitHub installation-token manager (daemon-github-clone). The signalr
  // surface needed is only `getRepoAccessToken`; we hand it the whole client
  // to avoid a per-call adapter and let TokenManager hold a function-typed
  // dep that doesn't change on token rotation.
  buildTokenManager: (signalr, logger) =>
    createTokenManager({
      signalr: {
        getRepoAccessToken: (repoFullName) => signalr.getRepoAccessToken(repoFullName),
      },
      logger,
    }),

  // Runtime-curation live mutation (Spec 16 Card 6).
  //
  // Production wires the raw `ChildProcessExecutor` for supervisorctl. The
  // runtime base image is expected to grant passwordless `sudo -n` for the
  // agent user OR run the daemon with sufficient privilege to write to
  // /etc/supervisor/conf.d directly — that's a runtime-base-image concern,
  // not the daemon's. If a future card needs to prepend `sudo -n` here,
  // factor a `SudoExecutor` wrapper and swap it in.
  buildExecutor: () => new ChildProcessExecutor(),
  // confDir override: the runtime base image's /etc/supervisor/supervisord.conf
  // ships with `[include] files = /data/.glenn/supervisor.d/*.conf`. The
  // default `/etc/supervisor/conf.d/` is NOT in that include glob, so confs
  // written there are silently ignored by supervisord — `reread` reports
  // "No config updates" and the service never starts. Pin to the include
  // directory so the conf is actually picked up.
  buildSupervisordController: (executor, logger) =>
    new SupervisordController({
      executor,
      fs: { readFile, writeFile, access, readdir, unlink },
      logger,
      confDir: '/data/.glenn/supervisor.d',
    }),
  buildRuntimeSpecApplier: (signalr, supervisord, executor, logger, hashStore, emitter) =>
    new RuntimeSpecApplier({ signalr, supervisord, executor, logger, hashStore, emitter }),

  // RuntimeEvent fan-out — singleton emitter (Spec runtime-spec-v2 "Event
  // taxonomy"). Hooks onto the SignalR client's connected/disconnected
  // lifecycle for buffer-drain semantics.
  buildRuntimeEventEmitter: (signalr, logger) =>
    new DefaultRuntimeEventEmitter({ signalr, logger }),

  // On-demand log tailer (runtime-spec-v2 Phase 5). Bind the SignalR client's
  // typed outbound proxy to the tailer's onLine sink at construction time so
  // every line read off `tail -F` lands on the hub without per-line plumbing.
  // The tailer stamps the timestamp as ISO-8601 (string); we re-hydrate to a
  // `Date` here because the generated DTO uses the C# DateTime shape.
  buildLogTailer: (signalr, logger) =>
    new LogTailer({
      onLine: ({ serviceName, line, timestamp }) =>
        signalr.sendServiceLogLine({
          serviceName,
          line,
          timestamp: new Date(timestamp),
        }),
      logger,
    }),

  // Long-lived supervisord poller that fires ServiceCrashed / ServiceRestarted
  // when transitions cross meaningful boundaries. Started post-bootstrap so it
  // doesn't double-up on StartingServicesStage's ServiceRunning fan-out.
  buildServiceStatusPoller: (supervisord, emitter, signalr, logger) =>
    new ServiceStatusPoller({ supervisord, emitter, signalr, logger }),

  exit: (code) => process.exit(code),
}

// ============================================================================
// Public entry point
// ============================================================================

/**
 * Run the daemon's Scene 1 startup sequence. Returns once `daemon ready` is
 * logged and the process is fully wired — control then sits on the heartbeat
 * + SignalR event loop until a shutdown signal arrives.
 *
 * Tests pass a `Partial<MainDeps>` to substitute fakes for any subset of the
 * module factories. Anything not overridden uses the production wiring.
 *
 * On unrecoverable errors this calls `deps.exit(1)` and returns; it does NOT
 * throw, because the production callers (the bottom-of-file `if
 * (import.meta.url === …)` branch) wrap with a top-level catch anyway and a
 * thrown-from-startup error would just become a less-readable exit-1 path.
 */
export async function runMain(overrides: Partial<MainDeps> = {}): Promise<void> {
  const deps: MainDeps = { ...defaultMainDeps, ...overrides }

  // Step 1 — DaemonConfig.fromEnv()
  let config: DaemonConfig
  try {
    config = deps.loadConfig()
  } catch (err) {
    // No logger yet — we can't get pino without a config. Use console as a
    // last resort. Operator sees the validation problems collected by
    // DaemonConfigError directly.
    // eslint-disable-next-line no-console -- pre-logger error path
    console.error('daemon: failed to load config:', err instanceof Error ? err.message : err)
    deps.exit(1)
    return
  }

  // Step 2 — pino logger
  const logger = deps.buildLogger(config)
  logger.info({ daemonVersion: DAEMON_VERSION, runtimeId: config.runtimeId }, 'daemon starting')

  // Step 2a — self-watchdog (Fix #3 / `heartbeat/SelfWatchdog.ts`). Started as
  // early as possible so it covers as much of the boot sequence as we can —
  // bootstrap I/O, supervisord spin-up, the works. If main's event loop ever
  // stalls > 25s the worker SIGKILLs the process so entrypoint.sh respawns us
  // BEFORE the master's HeartbeatWatcher (30s) flags the runtime as Crashed
  // and Fly destroys the machine. See SelfWatchdog.ts for the rationale.
  const selfWatchdog = deps.buildSelfWatchdog(logger)
  selfWatchdog.start()

  // Step 3 — SignalRClient + start. Failure → exit(1) (supervisord respawns).
  const signalr = deps.buildSignalR(config, logger)
  try {
    await signalr.start()
  } catch (err) {
    logger.error({ err }, 'failed to start signalr')
    deps.exit(1)
    return
  }

  // Expose signalr to the module-level crash handlers (installed at CLI entry).
  // Without this, the handlers can only emit to stderr — which Fly buffers and
  // we have no straightforward way to read from this side. With it, uncaught /
  // unhandledRejection crashes ship a RuntimeErrorReports row before exit so
  // the operator sees `category=daemon_crash` + stack trace in the DB without
  // needing Fly log access.
  crashReporterSignalR = signalr
  crashReporterLogger = logger

  // Env-var manager (Spec 14 Card 7). Built BEFORE the orchestrator so
  // WritingConfigStage can call `loadInitial` during bootstrap. The
  // UpdateConfig handler below also references it for runtime delta apply.
  const envVarManager = deps.buildEnvVarManager(config, logger)

  // MCP registry (Spec 15 Card 5). Built BEFORE the orchestrator so
  // WritingConfigStage can call `loadInitial` during bootstrap, and BEFORE
  // TurnRunner so the SDK factory can read `entries()` on every turn.
  const mcpRegistry = deps.buildMcpRegistry()

  // GitHub installation-token manager (daemon-github-clone). Built here so
  // it can be threaded into BootstrapOrchestrator → CloningRepoStage. The
  // SignalR client is already connected (Step 3) so its `getRepoAccessToken`
  // wrapper is callable; the first actual call happens inside CloningRepoStage.
  const tokenManager = deps.buildTokenManager(signalr, logger)

  // Step 3b — singleton RuntimeEventEmitter (runtime-spec-v2 "Event taxonomy").
  // Wired AFTER signalr.start() so onConnected fires on reconnect (drain), and
  // BEFORE buildBootstrap so every bootstrap stage can emit through it. The
  // emitter never throws; failures are logged + swallowed inside.
  const runtimeEventEmitter = deps.buildRuntimeEventEmitter(signalr, logger)

  // Step 4 — BootstrapOrchestrator. The shared `BootstrapState` carrier is
  // hoisted here (rather than constructed inside the factory) so the same
  // instance can be threaded into the GitModule wiring below. GitModule's
  // `getRepoFullName` closure reads `state.payload.repo.url` at push time
  // to derive the GitHub installation-token cache key — bootstrap has
  // populated `state.payload` long before the first auto-commit fires
  // (push happens on `turn idle`, which can only land post-bootstrap).
  //
  // Persistent boot-retry counter (runtime-observability-super-admin). Loaded
  // BEFORE building the orchestrator so the current attempt number is stamped
  // on every BootstrapStage* event payload. The counter lives on the runtime's
  // /data volume so a supervisord respawn or Fly machine restart sees the
  // previous value rather than starting fresh.
  const bootRetryCounter = new BootRetryCounter({ logger })
  await bootRetryCounter.loadAndIncrement()
  const bootstrapState = new BootstrapState()
  // Shared in-memory boot-issue collector (self-healing-runtime-specs, D1).
  // Constructed here in runMain so the SAME instance can later be threaded into
  // D2's `get_boot_issues` MCP tool (via buildCustomTools) — the orchestrator
  // writes degraded-spec issues into it; the agent self-heal loop reads them.
  const bootIssues = new BootIssueStore()

  // === Self-heal tool wiring (self-healing-runtime-specs, card D2) ===
  //
  // The three self-heal MCP tools (`get_runtime_spec`, `get_boot_issues`,
  // `request_rebootstrap`) bind to live daemon state via the callbacks below.
  //
  //   - getRuntimeSpec reads the SAME in-memory `bootstrapState` the
  //     orchestrator populated in FetchingStage (the spec that booted, plus
  //     the payload envelope version). Returns null until FetchingStage runs.
  //   - listBootIssues reads the SAME `bootIssues` store the orchestrator
  //     records degraded-spec issues into.
  //   - triggerRebootstrap reuses the EXISTING force-rebootstrap teardown
  //     (defined once below as `triggerForceRebootstrap` and ALSO wired to
  //     `signalr.onForceRebootstrap`) — no new mechanism. The reference is
  //     resolved at fire time (it's assigned before any turn — hence any tool
  //     call — can run).
  let triggerForceRebootstrap: (reason: string) => Promise<void> = async (reason) => {
    // Defensive fallback: a rebootstrap requested before the real teardown is
    // wired (cannot happen in practice — tools run only after bootstrap setup
    // completes) just logs rather than throwing into the agent's tool result.
    logger.warn({ reason }, 'triggerForceRebootstrap called before wiring; ignoring')
  }
  const selfHealTools = {
    getRuntimeSpec: (): { version: string; runtimeSpec: unknown } | null => {
      if (!bootstrapState.hasPayload()) return null
      const payload = bootstrapState.payload
      return { version: payload.version, runtimeSpec: payload.runtimeSpec }
    },
    listBootIssues: (): BootIssue[] => bootIssues.list(),
    triggerRebootstrap: (reason: string): Promise<void> => triggerForceRebootstrap(reason),
  }

  const bootstrap = deps.buildBootstrap(
    config,
    signalr,
    envVarManager,
    mcpRegistry,
    tokenManager,
    bootstrapState,
    logger,
    runtimeEventEmitter,
    bootRetryCounter,
    bootIssues,
  )

  // Step 5 — observers + the actor (TurnRunner) + lifecycle deps
  const disk = deps.buildDiskMonitor(logger)

  // Fetch the `propose_runtime_spec` description + JSON schema from the
  // backend ONCE at startup. Source of truth is the live ServicePresets
  // registry, so a new preset in super admin reaches the agent without a
  // daemon release. fetchToolDescription returns a safe fallback on any
  // error — boot is never blocked on backend availability.
  const proposeRuntimeSpec = await deps.fetchToolDescription(config, logger)

  const customTools = deps.buildCustomTools(config, logger, proposeRuntimeSpec, selfHealTools)
  const daemonToolsMcpServer = deps.buildDaemonToolsMcpServer(config, customTools, logger)
  await daemonToolsMcpServer.start()

  const turnRunner = deps.buildTurnRunner(
    config,
    signalr,
    mcpRegistry,
    daemonToolsMcpServer,
    customTools,
    logger,
  )
  const quietMode = deps.buildQuietMode(config, turnRunner, logger)

  // Build the supervisord XML-RPC client once and share. Used here for the
  // ProcessStatsCollector's pid enumeration (top-N process RSS/CPU%) and
  // later for the ServiceStatusPoller. Construction is pure — failures only
  // surface on the first call(), where each consumer catches + degrades.
  const supervisordXmlRpc = new SupervisordXmlRpcClient({ logger })

  // runtime-observability-super-admin — per-process RSS/VmSize + CPU% +
  // aggregate network rx/tx rates, sampled every 30s and read by
  // HeartbeatModule.gather() for the runtime drawer.
  const processStats = new ProcessStatsCollector({
    supervisord: supervisordXmlRpc,
    logger,
  })
  processStats.start()

  const heartbeat = deps.buildHeartbeat(
    config,
    signalr,
    turnRunner,
    disk,
    quietMode,
    logger,
    processStats,
  )

  // Liveness worker (Fix #1) — second, thread-independent path to
  // ProjectRuntime.LastHeartbeatAt. Constructed alongside the main-thread
  // HeartbeatModule; started together at Step 9. See
  // `heartbeat/LivenessWorker.ts` for the why.
  const livenessWorker = deps.buildLivenessWorker(config, logger)

  // Step 5b — hook subsystem (Card 10 of daemon-hooks-runner). Built here so
  // (a) ShutdownCoordinator can stop the file watcher alongside other
  // observers and (b) the listeners we wire below can fire from the very
  // first turn `idle` event.
  const hookExecutor = deps.buildHookExecutor(config, logger)
  const hooksModule = deps.buildHooksModule(hookExecutor, logger)
  const fileWatcher = deps.buildFileChangeWatcher(logger)
  const hookEmitter = deps.buildHookEventEmitter(signalr, config.runtimeId, logger)
  const selfHeal = deps.buildSelfHealCoordinator(
    signalr,
    hookEmitter,
    config.runtimeId,
    logger,
  )

  // Step 5c — git subsystem (Card 10 of daemon-git-ops). Order matters:
  //   - SshKeyHandler is independent.
  //   - GitRunner needs an `onAudit` callback that forwards into GitModule.
  //     GitModule is constructed AFTER the runner, so we use a lazy reference
  //     (`gitModuleRef`) populated post-construction. Same pattern hooks uses
  //     for emitter ↔ self-heal cross-references.
  //   - DestructiveOpGate + PushRetryJob both depend on GitModule and live
  //     downstream of it.
  const sshKeyHandler = deps.buildSshKeyHandler(logger)
  let gitModuleRef: GitModule | undefined
  const gitRunner = deps.buildGitRunner(GIT_CWD, config, logger, (e) => {
    gitModuleRef?.handleRunnerAudit(e)
  })
  // `getRepoFullName` reads the bootstrap-fetched payload lazily on every
  // push — bootstrap completes before any `idle` event (the only auto-commit
  // trigger) so the payload is always populated by the time this fires.
  // Returns `null` when the runtime has no repo (the AI-curated empty-spec
  // path) or when the repo URL fails the strict `https://github.com/owner/repo`
  // shape check (logged + treated as "no auth available" — the push will
  // still attempt and fail loud rather than silently no-op).
  const getRepoFullName = (): string | null => {
    if (!bootstrapState.hasPayload()) return null
    const repo = bootstrapState.payload.repo
    if (repo === null || repo === undefined) return null
    try {
      return parseRepoFullName(repo.url)
    } catch (err) {
      logger.warn({ err, repoUrl: repo.url }, 'getRepoFullName: parse failed')
      return null
    }
  }
  const gitModule = deps.buildGitModule(
    gitRunner,
    signalr,
    tokenManager,
    getRepoFullName,
    logger,
  )
  gitModuleRef = gitModule
  // daemon-git-sync-redesign Card 5: TurnRunner needs GitModule to run the
  // first-session FF pull when StartTurn carries `pullBeforeStart=true`.
  // TurnRunner is constructed BEFORE GitModule (the GitRunner ↔ GitModule
  // audit-callback cycle forces that order), so we wire the module via a
  // late-binding setter here once both objects exist.
  turnRunner.setGitModule(gitModule)
  const destructiveOpGate = deps.buildDestructiveOpGate(gitModule, signalr, logger)
  const pushRetryJob = deps.buildPushRetryJob(
    config,
    gitModule,
    signalr,
    quietMode,
    logger,
  )

  // Step 5d — on-demand log tailer (runtime-spec-v2 Phase 5). Built BEFORE
  // bootstrap so a server-pushed `StartLogTail` arriving mid-bootstrap (rare
  // but possible — operators sometimes open the Logs tab while the runtime is
  // still Booting) lands on a live handler rather than the SignalR client's
  // unhandled-method warning path. The tailer itself is purely lazy — it
  // doesn't spawn anything until `startTail` is invoked.
  const logTailer = deps.buildLogTailer(signalr, logger)
  signalr.onStartLogTail((serviceName) => {
    logTailer.startTail(serviceName)
  })
  signalr.onStopLogTail((serviceName) => {
    logTailer.stopTail(serviceName)
  })

  // runtime-observability-super-admin — daemon-log tail. The daemon runs
  // under supervisord (`[program:agent]`) which writes its stdout / stderr
  // to `/var/log/supervisor/agent.{out,err}.log`. We piggy-back on
  // LogTailer's ref-counting + tail-F machinery but point it at `/var/log
  // /supervisor` with `agent.out` / `agent.err` as the "service names" —
  // those resolve to `/var/log/supervisor/agent.out.log` + `agent.err.log`
  // (LogTailer already appends `.log` per its naming convention). Each
  // line is rebadged with the correct `stream` field before going to the
  // hub. `initialLines: 200` so the first subscriber immediately sees the
  // last ~200 lines of context instead of waiting for new output.
  const DAEMON_LOG_OUT = 'agent.out'
  const DAEMON_LOG_ERR = 'agent.err'
  const daemonLogTailer = new LogTailer({
    onLine: ({ serviceName, line, timestamp }) => {
      const stream =
        serviceName === DAEMON_LOG_ERR ? 'stderr' : 'stdout'
      return signalr.sendDaemonLogLine({
        stream,
        line,
        timestamp: new Date(timestamp),
      })
    },
    logger,
    initialLines: 200,
  })
  signalr.onStartDaemonLogTail(() => {
    daemonLogTailer.startTail(DAEMON_LOG_OUT)
    daemonLogTailer.startTail(DAEMON_LOG_ERR)
  })
  signalr.onStopDaemonLogTail(() => {
    daemonLogTailer.stopTail(DAEMON_LOG_OUT)
    daemonLogTailer.stopTail(DAEMON_LOG_ERR)
  })

  // Step 5e — server-push receivers that must be registered BEFORE we yield
  // the event loop with `await bootstrap.start(...)`. The hub's
  // `OnConnectedAsync` pushes a bootstrap `UpdateConfig` immediately on
  // connect; that message arrives during the bootstrap awaits and is
  // silently dropped if its handler isn't on `#handlers` yet. Symptom: the
  // daemon's `gitModule` stays at its `autoCommit: false` default → the
  // turn-idle auto-commit listener early-returns → no commit/push ever runs.
  // Same race applies to the other server-initiated handlers below; they
  // just bit auto-commit first because that's what the user is actively
  // exercising. Construct dependencies (executor / supervisord / applier /
  // custom tools) here too so the handler closures can capture them.
  const executor = deps.buildExecutor()
  const supervisordController = deps.buildSupervisordController(executor, logger)
  // Share the install-hash cache between bootstrap (InstallStage) and the
  // live mutation path (RuntimeSpecApplier) — both read/write the same
  // file. A fresh instance is cheap; the store keeps no in-memory state.
  const applierHashStore = new InstallHashStore()
  const runtimeSpecApplier = deps.buildRuntimeSpecApplier(
    signalr,
    supervisordController,
    executor,
    logger,
    applierHashStore,
    runtimeEventEmitter,
  )
  // The MainDeps.buildCustomTools factory differs from the inline `customTools`
  // we built in Step 5 only in its approval wiring (this one auto-approves
  // server-initiated restarts). It still needs the same tool description,
  // which we already fetched once above.
  const customToolsForServerInit = deps.buildCustomTools(config, logger, proposeRuntimeSpec, selfHealTools)

  // Bootstrap seed for the hook subsystem. Empty until backend Card 4's
  // bootstrap delivery wires real config via UpdateConfig on connect. Seeded
  // BEFORE the onUpdateConfig handler is registered so a server-pushed
  // UpdateConfig that arrives during bootstrap can't be wiped by a later
  // empty-seed call.
  hooksModule.setConfig(HOOK_CONFIG_EMPTY)
  fileWatcher.setHooks([])

  // UpdateConfig — multiple concerns ride on the same wire DTO:
  //   1. Token rotation (v1, fire-and-forget — accessTokenFactory in
  //      SignalRClient re-reads on next reconnect).
  //   2. Hook config hot-swap (Card 10 of daemon-hooks-runner).
  //   3. Auto-commit policy + deploy-key rotation (Card 10 of daemon-git-ops).
  //
  // The hook fields were added tolerantly when the wire DTO didn't declare
  // them; the daemon-git-ops fields are now declared on `ConfigUpdatePayload`
  // so we read them via the typed surface where possible.
  signalr.onUpdateConfig(async (p: ConfigUpdatePayload) => {
    // `!= null` (loose) covers both `null` and `undefined` — the .NET hub
    // sends `null` to mean "don't apply this field"; a strict `!== undefined`
    // check lets the null through and downstream code crashes trying to
    // `.split` / `.length` on it. The handler-exception isolation in
    // SignalRClient catches the crash but aborts the rest of the handler
    // body, so a later field never gets applied (e.g. autoCommit).
    if (p.runtimeToken != null) {
      try {
        config.rotateToken(p.runtimeToken)
        // Fix #1: forward to LivenessWorker so its thread-independent
        // POST /heartbeat-tick uses the fresh JWT on the next beat. The
        // worker holds its own token copy on the parallel OS thread; if
        // we forget this line the worker keeps using the boot-time token
        // until it expires and then starts returning 401, silently
        // breaking the second liveness path.
        livenessWorker.rotateToken(p.runtimeToken)
        logger.info('runtime token rotated')
      } catch (err) {
        logger.warn({ err }, 'token rotation rejected (malformed)')
      }
    }
    if (typeof p.hooksJson === 'string') {
      try {
        const parsed = JSON.parse(p.hooksJson) as HookConfig
        hooksModule.setConfig(parsed)
        fileWatcher.setHooks(
          (parsed.onFileChange ?? []).map((h) => ({ name: h.name, pattern: h.pattern })),
        )
        logger.info(
          {
            counts: {
              beforePrompt: parsed.beforePrompt?.length ?? 0,
              afterPrompt: parsed.afterPrompt?.length ?? 0,
              onFileChange: parsed.onFileChange?.length ?? 0,
              beforeCommit: parsed.beforeCommit?.length ?? 0,
            },
          },
          'hooks config updated',
        )
      } catch (err) {
        logger.warn({ err }, 'invalid hooksJson — ignoring')
      }
    }
    if (typeof p.hooksKillSwitch === 'boolean') {
      hooksModule.setKillSwitch(p.hooksKillSwitch)
      logger.info({ disabled: p.hooksKillSwitch }, 'hook kill switch updated')
    }
    // Auto-commit policy (Card 10 of daemon-git-ops). Hot-swappable; the
    // turn-idle handler reads `gitModule.isAutoCommit()` at the moment of
    // emission so a change here lands on the next idle.
    if (typeof p.autoCommit === 'boolean') {
      gitModule.setAutoCommit(p.autoCommit)
    }
    // Deploy-key rotation (Card 10 of daemon-git-ops). `null` is "leave the
    // existing key alone" — see SshKeyHandler. Failure to write the key is
    // logged but does NOT fail the whole UpdateConfig handler; other concerns
    // (token rotation, hook config) on the same payload have already been
    // applied.
    if (p.deployKey != null) {
      try {
        await sshKeyHandler.applyConfig({ deployKey: p.deployKey })
      } catch (err) {
        logger.warn({ err }, 'deploy key rotation failed')
      }
    }
    // Env-var delta (Spec 14 Card 7). Empty-or-absent is a no-op so backend
    // can omit the field without forcing the daemon to think.
    if (p.envVarsDelta != null && p.envVarsDelta.length > 0) {
      try {
        await envVarManager.applyDelta(p.envVarsDelta)
      } catch (err) {
        // Reject paths (newline in value, mkdir/rename failures, …) are
        // logged but never fail the whole UpdateConfig handler — token
        // rotation and other concerns above have already landed.
        logger.warn({ err }, 'env vars delta apply failed')
      }
    }
  })

  // ExecuteDestructiveGitOp / MergeBranch — server-initiated git inbound
  // handlers (Card 9 + Card 10 of daemon-git-ops). The first looks up a
  // previously-parked approval and runs it; the second is a "user already
  // approved by clicking merge in the UI" delegate to GitModule.merge.
  signalr.onExecuteDestructiveGitOp(async (p: ExecuteDestructiveGitOpPayload) => {
    if (typeof p.opId !== 'string' || p.opId === '') {
      logger.warn({ payload: p }, 'invalid ExecuteDestructiveGitOp payload (no opId)')
      return
    }
    await destructiveOpGate.handleExecuteApproved(p.opId)
  })

  signalr.onMergeBranch(async (p: MergeBranchPayload) => {
    await destructiveOpGate.handleMergeBranch(p)
  })

  // GetChangedFiles / GetFileDiff — Phase 1 of diff-view-tab spec.
  // Server-initiated request/response (the user clicked the Changes tab).
  // Both methods route through GitModule's serialisation queue so a read
  // can't race a concurrent commit / rebase / merge. The runtimeId arg from
  // the wire is unused: the daemon's config already pins it to a single
  // runtime, and GitModule's #cwd is the project repo for that runtime —
  // a mismatch would mean a hub-routing bug we want to know about, not
  // silently fork on. We trust the SignalR group binding (one daemon per
  // runtime-{id} group) and ignore the arg.
  //
  // The shape conversion at the boundary — `null` → `undefined` — is
  // because Tapper transpiles C# `string?` as TS `string?:` (optional),
  // not `string | null`. The DiffQueries module uses `null` (matching the
  // wire reality of every other daemon-emitted payload); we normalise
  // here so the typed-receiver signature compiles.
  signalr.onGetChangedFiles(async (_runtimeId, _req) => {
    const r = await gitModule.getChangedFiles()
    return {
      scope: r.scope,
      ...(r.base !== null ? { base: r.base } : {}),
      ...(r.head !== null ? { head: r.head } : {}),
      totalAdditions: r.totalAdditions,
      totalDeletions: r.totalDeletions,
      files: r.files.map((f) => ({
        path: f.path,
        ...(f.oldPath !== null ? { oldPath: f.oldPath } : {}),
        status: f.status,
        additions: f.additions,
        deletions: f.deletions,
        isBinary: f.isBinary,
        ...(f.sizeBytes !== null ? { sizeBytes: f.sizeBytes } : {}),
      })),
      ...(r.reason !== null ? { reason: r.reason } : {}),
    }
  })

  signalr.onGetFileDiff(async (_runtimeId, req) => {
    const r = await gitModule.getFileDiff(req.path)
    return {
      path: r.path,
      status: r.status,
      isBinary: r.isBinary,
      isTruncated: r.isTruncated,
      ...(r.unifiedDiff !== null ? { unifiedDiff: r.unifiedDiff } : {}),
      ...(r.reason !== null ? { reason: r.reason } : {}),
    }
  })

  // Phase-3 (compare-base) handlers — branch-scope diff variants of the
  // working-tree ones above. Same `_runtimeId` is-ignored rationale (one
  // daemon per runtime group); same `null → undefined` boundary normalisation
  // because Tapper transpiles C# `string?` as TS optional, while DiffQueries
  // uses `null` to match wire-reality of other daemon payloads.
  signalr.onGetBranchChangedFiles(async (_runtimeId, baseRef, headRef) => {
    const r = await gitModule.getBranchChangedFiles(baseRef, headRef)
    return {
      scope: r.scope,
      ...(r.base !== null ? { base: r.base } : {}),
      ...(r.head !== null ? { head: r.head } : {}),
      totalAdditions: r.totalAdditions,
      totalDeletions: r.totalDeletions,
      files: r.files.map((f) => ({
        path: f.path,
        ...(f.oldPath !== null ? { oldPath: f.oldPath } : {}),
        status: f.status,
        additions: f.additions,
        deletions: f.deletions,
        isBinary: f.isBinary,
        ...(f.sizeBytes !== null ? { sizeBytes: f.sizeBytes } : {}),
      })),
      ...(r.reason !== null ? { reason: r.reason } : {}),
    }
  })

  signalr.onGetBranchFileDiff(async (_runtimeId, baseRef, headRef, path) => {
    const r = await gitModule.getBranchFileDiff(baseRef, headRef, path)
    return {
      path: r.path,
      status: r.status,
      isBinary: r.isBinary,
      isTruncated: r.isTruncated,
      ...(r.unifiedDiff !== null ? { unifiedDiff: r.unifiedDiff } : {}),
      ...(r.reason !== null ? { reason: r.reason } : {}),
    }
  })

  signalr.onGetCommitRange(async (_runtimeId, baseRef, headRef, limit) => {
    const commits = await gitModule.getCommitRange(baseRef, headRef, limit)
    return {
      commits: commits.map((c) => ({
        sha: c.sha,
        message: c.message,
        authorDate: c.authorDate,
        authorName: c.authorName,
      })),
    }
  })

  // ApplyRuntimeSpecDelta — server-initiated live mutation (Spec 16 Card 6).
  // RuntimeSpecApplier serialises concurrent applies via #chain and acks
  // back via RuntimeSpecDeltaApplied; failures are reported via the ack so
  // the daemon stays up even if supervisord falls over.
  signalr.onApplyRuntimeSpecDelta(async (p: ApplyRuntimeSpecDeltaPayload) => {
    // Fire-and-forget from the SignalR side — `applyDelta` never rejects
    // (it acks failures via `RuntimeSpecDeltaApplied` instead). The
    // `.catch` is belt-and-braces in case a future change makes it possible
    // to reject; we'd never want an unhandled rejection from this handler.
    runtimeSpecApplier.applyDelta(p).catch((err) => {
      logger.error({ err, proposalId: p.proposalId }, 'unhandled apply failure')
    })
  })

  // ForceRebootstrap — tear the daemon down and respawn so the next
  // `FetchingStage` pulls a fresh `BootstrapPayloadV2`. We abort any in-flight
  // bootstrap first, then delegate to the existing shutdown coordinator (which
  // drains turns, stops timers, emits the shutdown event, closes SignalR) and
  // finally exit so supervisord respawns us. Closure references to
  // `bootstrapController` / `shutdownCoordinator` resolve at fire time — by the
  // time a ForceRebootstrap message (or a self-heal tool call) can land, both
  // have been initialised below.
  //
  // This teardown is hoisted into `triggerForceRebootstrap` (assigned to the
  // `let` declared up in Step 4) so card D2's `request_rebootstrap` MCP tool
  // reuses the EXACT same path as the server-initiated push — no second
  // mechanism. Both the SignalR handler and the tool funnel through here.
  triggerForceRebootstrap = async (reason: string): Promise<void> => {
    logger.info({ reason }, 'daemon.force_rebootstrap')
    try {
      bootstrapController.abort()
    } catch (err) {
      logger.warn({ err }, 'bootstrapController.abort threw (continuing)')
    }
    try {
      await shutdownCoordinator.shutdown('force_rebootstrap')
    } catch (err) {
      logger.warn({ err }, 'shutdownCoordinator.shutdown threw (continuing)')
    }
    // `shutdown()` already calls `deps.exit(0)` internally, but we mirror the
    // existing exit-after-fatal pattern here as a belt-and-braces guard in
    // case a future change makes shutdown() return without exiting.
    deps.exit(0)
  }
  signalr.onForceRebootstrap(async (p: ForceRebootstrapPayload) => {
    logger.info(
      { reason: p.reason, initiatedAt: p.initiatedAt },
      'daemon.force_rebootstrap_received',
    )
    await triggerForceRebootstrap(p.reason)
  })

  // chat-file-attachments — server pushes `StageAttachment` the moment an R2
  // upload completes; the daemon downloads to the runtime's local FS path
  // (under /data/project/repo/.glenn/, gitignored) and acks via
  // `RuntimeHub.ReportAttachmentStaged`. The stager never throws (every
  // failure goes through the ack), so this handler is fire-and-forget; the
  // `.catch` defends against a future change. Wired alongside the other
  // server-push handlers so it's live before bootstrap finishes.
  const attachmentStager = new AttachmentStager({
    hub: {
      invoke: async (method, attachmentId, success, error) => {
        await signalr.invoke(method, attachmentId, success, error)
      },
    },
    logger,
  })
  signalr.onStageAttachment((p) => {
    attachmentStager.stage(p).catch((err) => {
      logger.error({ err, attachmentId: p.attachmentId }, 'unhandled stage failure')
    })
  })

  // RestartService — server-initiated restart. Reuses the same in-process
  // tool path as the SDK's restart_service tool — no separate code path.
  signalr.onRestartService(async (p: RestartServicePayload) => {
    const tool = customToolsForServerInit.find((t) => t.name === 'restart_service')
    if (tool === undefined) {
      logger.warn('RestartService received but restart_service tool not registered')
      return
    }
    // The tool's schema requires { name, reason }; map from the wire-payload
    // shape (which uses `serviceName`). The tool's ToolContext requires a
    // sessionId/turnId — we have neither for a server-initiated restart, so
    // pass requestId as a correlator in turnId and leave sessionId empty.
    try {
      await tool.run(
        { name: p.serviceName, reason: p.reason },
        {
          signalr,
          config,
          sessionId: '',
          turnId: p.requestId,
        },
      )
    } catch (err) {
      logger.error({ err, requestId: p.requestId }, 'restart_service failed')
    }
  })

  const shutdownCoordinator = deps.buildShutdownCoordinator(
    config,
    turnRunner,
    heartbeat,
    livenessWorker,
    disk,
    quietMode,
    signalr,
    logger,
    fileWatcher,
    pushRetryJob,
    destructiveOpGate,
    logTailer,
  )

  // Step 6 — install signal handlers BEFORE bootstrap. A SIGTERM that arrives
  //          while bootstrap is in flight aborts the BootstrapOrchestrator's
  //          AbortController so the async `start(signal)` unwinds cleanly.
  const bootstrapController = new AbortController()
  shutdownCoordinator.install({
    cancelInFlightBootstrap: () => bootstrapController.abort(),
  })

  // Step 7 — wire StartTurn / CancelTurn handlers BEFORE bootstrap.
  //
  // Race the previous ordering hit: ReportReadyStage (the final bootstrap
  // stage) `await`s `signalr.runtimeReady()`. The .NET hub method synchronously
  // runs RuntimeReadyCommand → flips the runtime Online → fires
  // DispatchQueuedSessionsOnRuntimeOnlineHandler → fans `StartTurn` to the
  // daemon's group, all BEFORE returning the RPC ack. The daemon receives
  // StartTurn on the same WebSocket; if our handler isn't registered yet, the
  // message is silently dropped and the session sits Running with
  // StartedAt=NULL forever. Symptom: prompts queued while the runtime is
  // Booting are never picked up, even after the runtime reports Online.
  //
  // The handlers are stateless wire-ups (`onStartTurn` / `onCancelTurn`) and
  // `#acceptingNewTurns` is `true` from construction, so wiring them now is
  // safe even though bootstrap hasn't finished. The .NET server gates dispatch
  // on RuntimeState=Online — which it isn't until ReportReadyStage runs — so
  // no real StartTurn can arrive before bootstrap completes.
  turnRunner.start()

  // Step 8 — bootstrap. Failure → emit `bootstrap_failed`, escalate the
  // terminal cases to the typed `ReportError` hub method so the audit trail
  // lands as a `RuntimeErrorReport` row (rather than just an AssistantText
  // carrier event the operator has to grep for), then exit(1).
  //
  // Two distinct terminal conditions short-circuit the supervisord respawn
  // loop:
  //
  //   1. A stage returned `{ ok: false, recoverable: false }` — it knows it's
  //      hosed (e.g. malformed bootstrap payload, runtime binary missing
  //      from R2 with a definite 404). No amount of retry will help.
  //
  //   2. The persistent `bootAttemptNumber` counter has exceeded
  //      `MAX_BOOT_ATTEMPTS` (10) — recoverable failures retried 10+ times
  //      across daemon respawns have looped long enough; treat as terminal.
  //      Pre-fix production saw smoketest-mongo hit 104+.
  //
  // Both cases surface as `BootstrapAbortedError({ terminal: true })`. We use
  // a single `bootstrap_terminal` category for the error report so the
  // backend (heartbeat-watcher → Crashed transition →
  // `ScheduleRespawnHandler`) and the operator dashboard can both pivot on
  // it. Non-terminal failures still get the legacy `bootstrap_failed` event
  // for observability but do NOT escalate — they may yet recover on the next
  // respawn (transient network blip, slow MAIN_API_URL cold-start, …).
  try {
    await bootstrap.start(bootstrapController.signal)
  } catch (err) {
    const reason = err instanceof Error ? err.message : String(err)
    const terminal = err instanceof BootstrapAbortedError && err.terminal
    logger.error({ err, terminal }, 'bootstrap failed')

    void emitRuntimeEvent(signalr, 'bootstrap_failed', { reason, terminal })

    if (terminal) {
      const abortErr = err as BootstrapAbortedError
      // Best-effort fatal signal so the .NET backend has a typed audit
      // row keyed off `category=bootstrap_terminal`. `sendErrorReport`
      // already swallows hub blips internally — we just await so the
      // invoke has a chance to flush before we exit. Awaiting briefly
      // is preferable to fire-and-forget because process.exit() can
      // cut the WebSocket mid-frame.
      try {
        await signalr.sendErrorReport({
          category: 'bootstrap_terminal',
          message: `Bootstrap terminal at stage '${abortErr.stage}' (attempts=${abortErr.attempts}): ${abortErr.reason}`,
          context: JSON.stringify({
            stage: abortErr.stage,
            attempts: abortErr.attempts,
            bootAttemptNumber: bootRetryCounter.current,
          }),
        })
      } catch (reportErr) {
        // Defensive: sendErrorReport already swallows, but belt + braces so
        // a never-resolving promise can't pin us out of exit.
        logger.warn({ err: reportErr }, 'sendErrorReport rejected unexpectedly; continuing to exit')
      }
    }

    deps.exit(1)
    return
  }

  // Per-turn self-heal iteration counts. Keyed by turnId; rolled forward on
  // continuation acceptance and pruned on rejection paths inside Card 9.
  // Sits in this scope so the closure captures it by reference for both the
  // idle handler and any future bootstrap-time hydration.
  const selfHealIterations = new Map<string, number>()

  // Long-lived AbortController for in-process hook runs. Distinct from
  // bootstrapController (whose lifetime ends when bootstrap.start() resolves)
  // because hooks run from any number of points (idle, fileWatcher) for the
  // entire daemon lifetime. Today the controller is never aborted — process
  // exit handles cleanup — but the seam exists so a future shutdown wire-up
  // can fire one signal that cancels every in-flight hook run at once.
  const shutdownAbort = new AbortController()

  // afterPrompt — runs at every turn `idle`. The TurnRunner's idle event
  // payload carries enough context for self-heal; missing/empty fields short-
  // circuit (server hasn't shipped them yet, or this idle is a synthetic one
  // for which hooks are nonsensical).
  turnRunner.on('idle', async (idleEvent) => {
    const { conversationId, turnId, agentId, skipHooks } = idleEvent
    if (!turnId || !conversationId || !agentId || skipHooks) {
      return
    }
    const result = await hooksModule.run('afterPrompt', {
      point: 'afterPrompt',
      conversationId,
      turnId,
      signal: shutdownAbort.signal,
      onEvent: (e) => hookEmitter.emitLifecycle(e, { conversationId, turnId }),
    })
    if (result.failures.length === 0) return

    const iteration = selfHealIterations.get(turnId) ?? 0
    const cont = await selfHeal.requestContinuation({
      conversationId,
      turnId,
      agentId,
      failures: result.failures.map((f) => ({
        hookName: f.spec.name,
        outputTail: f.result.outputTail,
      })),
      iteration,
    })
    if (cont.accepted && cont.newTurnId !== undefined) {
      selfHealIterations.set(cont.newTurnId, iteration + 1)
      selfHealIterations.delete(turnId)
    }
    // Rejection paths (budgetExhausted, maxedOut, …) are logged + emitted by
    // SelfHealCoordinator itself; nothing more to do here.
  })

  // Auto-commit on idle (Card 10 of daemon-git-ops). Separate listener so a
  // throw inside the auto-commit path can never block the hook/self-heal
  // listener registered above. Only fires when the policy is enabled, the
  // turn was a real (non-synthetic) one, and hooks weren't explicitly skipped
  // — `skipHooks` is the closest signal we have for "this turn shouldn't
  // touch the working tree" until the wire DTO grows a dedicated flag.
  turnRunner.on('idle', async (idleEvent) => {
    const { sessionId, conversationId, turnId, skipHooks, userPrompt } = idleEvent
    if (!turnId || !conversationId || skipHooks) return
    if (!gitModule.isAutoCommit()) return

    try {
      const message = buildCommitMessage({ userPrompt })
      const commitCtx = { conversationId, turnId }
      const commitResult = await gitModule.commit(message, commitCtx)
      if (!commitResult.ok || commitResult.noChanges === true) return

      // Resolve the branch we just committed on. GitModule already plucks it
      // from the commit output (best-effort); fall back to a live read if it
      // didn't land in the regex parse.
      let branch: string
      if (commitResult.branch !== undefined) {
        branch = commitResult.branch
      } else {
        try {
          branch = await gitModule.currentBranch()
        } catch (err) {
          logger.warn(
            { err, conversationId, turnId },
            'auto-commit: currentBranch failed, skipping push',
          )
          return
        }
      }

      const pushResult = await gitModule.push('origin', branch, commitCtx)
      if (!pushResult.ok) {
        pushRetryJob.recordFailure('origin', branch)
      }

      // P3.1 commit trailer — persist a single CommitMade event into the
      // session's chat history once both the commit AND the push attempt have
      // settled. The live SignalR fan-out (CommitMade + GitPushSucceeded /
      // GitPushFailed) stays untouched; this row is what makes the trailer
      // survive a page refresh. Best-effort: a failure to emit must not
      // poison the next turn — the commit + push already happened.
      //
      // Skipping when sessionId is empty covers two edge cases:
      //   - A synthetic-idle fired before any real turn ran (sessionId was
      //     defaulted to ''); there's no AgentSession row to attach to.
      //   - A future caller of TurnRunner that doesn't carry session
      //     correlation; the chat-history trailer is the only consumer of
      //     this event, so dropping it is safe.
      if (sessionId.length === 0) return
      const commitSha = commitResult.commitSha
      if (commitSha === undefined) return
      const pushFailureReason = pushResult.ok ? null : (pushResult.failureReason ?? 'Unknown')
      const trailerPayload = {
        commitSha,
        shortSha: commitSha.slice(0, 7),
        branch,
        message,
        fileCount: commitResult.fileCount ?? 0,
        pushed: pushResult.ok,
        pushFailureReason,
        conversationId,
        turnId,
      }
      try {
        // Post-cursor-native-chat-ux: CommitMade is no longer a first-class
        // AgentEventKind. We stamp the trailer as a run-level Status event
        // with the commit payload folded into `eventData` JSON. The chat
        // panel ignores Status frames without a runStatus column; consumers
        // that want commit trailers read them from the audit row's eventData.
        await signalr.emitEvent({
          sessionId,
          kind: AgentEventKind.Status,
          eventData: JSON.stringify({ subtype: 'CommitMade', ...trailerPayload }),
          emittedAt: new Date().toISOString(),
        })
      } catch (emitErr) {
        logger.warn(
          { err: emitErr, conversationId, turnId, commitSha },
          'auto-commit: failed to emit CommitMade trailer event (live commit/push already happened)',
        )
      }
    } catch (err) {
      // Auto-commit is best-effort — never let it crash the daemon. The turn
      // is already idle; logging is sufficient.
      logger.warn({ err, conversationId, turnId }, 'auto-commit failed')
    }
  })

  // onFileChange — debounced batches arrive from FileChangeWatcher; attribute
  // them to the in-flight turn (if any) so the audit trail can correlate.
  fileWatcher.on('changeBatch', async (_batch) => {
    const activeTurn = turnRunner.getActiveTurn()
    const ctx: {
      point: 'onFileChange'
      conversationId?: string
      turnId?: string
      signal: AbortSignal
      onEvent: (e: HookLifecycleEvent) => void
    } = {
      point: 'onFileChange',
      signal: shutdownAbort.signal,
      onEvent: (e) =>
        hookEmitter.emitLifecycle(e, {
          ...(activeTurn !== null ? { conversationId: activeTurn.conversationId } : {}),
          ...(activeTurn !== null ? { turnId: activeTurn.turnId } : {}),
        }),
    }
    if (activeTurn !== null) {
      ctx.conversationId = activeTurn.conversationId
      ctx.turnId = activeTurn.turnId
    }
    await hooksModule.run('onFileChange', ctx)
  })

  // Cloudflare Tunnel (cloudflare-tunnel-preview Phase 4). When the runtime
  // was provisioned for a project with a tunnel allocated, RuntimeProvisionerJob
  // stamps TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME onto the Fly machine.
  // We register a supervisord program for cloudflared so the tunnel comes up
  // alongside the project. If TUNNEL_TOKEN is missing/empty (legacy branches,
  // local dev, projects without a tunnel), this is a silent no-op.
  //
  // Reuses the same `executor` + `/data/.glenn/supervisor.d` confDir as
  // RuntimeSpecApplier above so any sudo wrapping (or future swap to a
  // permission-elevated executor) lands in both code paths consistently.
  const tunnelToken = process.env['TUNNEL_TOKEN'] ?? ''
  if (tunnelToken.length > 0) {
    const previewPort = parseInt(process.env['PREVIEW_PORT'] ?? '5173', 10)
    const previewHostname = process.env['PREVIEW_HOSTNAME']
    const cloudflaredController = new CloudflaredController({
      executor,
      fs: { readFile, writeFile, access },
      logger,
      confDir: '/data/.glenn/supervisor.d',
      // Health-check FSM (runtime-observability-super-admin B5): one HEAD per
      // minute against `https://<previewHostname>`, emitting
      // CloudflaredTunnelDown / CloudflaredTunnelUp on transitions.
      emitter: runtimeEventEmitter,
    })
    try {
      await cloudflaredController.apply({
        tunnelToken,
        previewPort,
        ...(previewHostname !== undefined ? { previewHostname } : {}),
      })
      logger.info(
        { hostname: previewHostname, previewPort },
        `[cloudflared] tunnel applied: ${previewHostname ?? '<no-hostname>'} -> localhost:${previewPort}`,
      )
      // Start the tunnel hostname health-check loop. Silent no-op when
      // previewHostname is missing (e.g. local dev branches that have
      // TUNNEL_TOKEN but no public FQDN).
      cloudflaredController.startHealthCheck(previewHostname)
    } catch (err) {
      // Tunnel setup is best-effort — a failure here must NOT crash the
      // daemon. The runtime still works for the agent + preview-by-port; only
      // the public Cloudflare-fronted URL is unavailable. Surface loudly so
      // operators can correlate with a missing preview.
      logger.error({ err }, '[cloudflared] tunnel apply failed')
    }
  } else {
    logger.info('[cloudflared] no TUNNEL_TOKEN - skipping tunnel')
  }

  // Phase D Card 3 — fan disk-pressure transitions up to main API. The
  // monitor only emits on level changes (ok→warn, warn→critical, …) so this
  // listener fires rarely under normal operations. We swallow invoke errors
  // because the .NET hub also persists the next sample's transition if this
  // one is dropped; daemons must not crash because a side-channel push failed.
  disk.on('pressure', (level, sample) => {
    const usedPct =
      sample.totalBytes > 0 ? (sample.usedBytes / sample.totalBytes) * 100 : 0
    void signalr
      .reportDiskPressure({
        level,
        usedBytes: sample.usedBytes,
        totalBytes: sample.totalBytes,
        usedPct,
        sampledAt: sample.sampledAt.toISOString(),
      })
      .catch((err: unknown) => {
        logger.warn(
          { err, level, usedPct },
          'reportDiskPressure failed; will rely on next transition',
        )
      })

    // runtime-observability-super-admin Task 1 — emit a structured runtime
    // event on transition into `critical` so the super-admin drawer's event
    // stream shows the moment a runtime crosses the danger threshold. The
    // monitor already debounces to transitions only, so this fires at most
    // once per ok/warn → critical edge. We do NOT emit for ok/warn (those
    // are routine and the periodic heartbeat snapshot is enough).
    if (level === 'critical') {
      runtimeEventEmitter.emit(RuntimeEventTypes.DiskPressureCritical, 'Error', {
        level,
        usedBytes: sample.usedBytes,
        totalBytes: sample.totalBytes,
        usedPct,
        sampledAt: sample.sampledAt.toISOString(),
      })
    }
  })

  // Step 9 — start the background timers + the file watcher. fileWatcher.start
  // is async (it awaits chokidar's `ready`); run it inline so the daemon does
  // not log "ready" until the watcher is observing FS events.
  heartbeat.start()
  // Fix #1: liveness worker runs on a separate OS thread, fetching
  // POST /api/runtimes/{id}/heartbeat-tick every `heartbeatIntervalMs`.
  // Even when main is starved by a heavy SDK turn, this thread keeps
  // ProjectRuntime.LastHeartbeatAt fresh — so the master never flips
  // us to Crashed. Started right after heartbeat.start() so the two
  // liveness paths come up together.
  livenessWorker.start()
  disk.start()
  quietMode.start()
  pushRetryJob.start()
  await fileWatcher.start()

  // Step 9b — ServiceStatusPoller (runtime-spec-v2 "Event taxonomy"). Started
  // AFTER bootstrap so the first poll sees a steady-state supervisord and
  // seeds without emitting spurious ServiceRestarted events. The poller runs
  // for the daemon's entire lifetime; no shutdown wiring today — supervisord
  // tears down on SIGTERM and the in-flight poll resolves naturally.
  // The supervisord XML-RPC client is hoisted earlier (Step 5b) so the
  // ProcessStatsCollector can share it with the poller. Failures from the
  // client are best-effort: each caller catches and treats as "no info".
  const serviceStatusPoller = deps.buildServiceStatusPoller(
    supervisordXmlRpc,
    runtimeEventEmitter,
    signalr,
    logger,
  )
  serviceStatusPoller.start()

  // Step 10 — ready.
  logger.info({ daemonVersion: DAEMON_VERSION }, 'daemon ready')
}

// ============================================================================
// Helpers
// ============================================================================

/**
 * Build the production pino logger. Pretty in dev (NODE_ENV !== 'production'),
 * structured JSON in prod. Level taken from DaemonConfig.
 *
 * The `pinoSecretRedactionOptions()` hook wires a streamWrite-stage scrub of
 * secret-shaped substrings (Stripe / OpenAI / AWS / Bearer tokens) into every
 * emitted line. Mirrors SecretValueRedactor on the .NET side; see
 * src/logging/secretRedactor.ts for the parity contract. Note that
 * `streamWrite` does NOT fire when the dev `pino-pretty` transport is in use
 * (transports run out-of-process and bypass hooks); structured-JSON prod logs
 * — the ones that ship to operators — do flow through the hook. Dev pretty
 * output is for local eyeballs only and is the lower-risk surface.
 */
function buildPinoLogger(config: DaemonConfig): Logger {
  const isDev = process.env['NODE_ENV'] !== 'production'
  if (isDev) {
    return pino({
      level: config.logLevel,
      transport: { target: 'pino-pretty' },
      ...pinoSecretRedactionOptions(),
    })
  }
  return pino({ level: config.logLevel, ...pinoSecretRedactionOptions() })
}

/**
 * Best-effort runtime-scope event emit. Mirrors the stopgap used by
 * BootstrapOrchestrator and ShutdownCoordinator: sessionId='', eventType
 * 'AssistantText' as a generic carrier, real event type inside `eventData`.
 *
 * TODO(runtime-bootstrap): replace once a real runtime-scope hub method
 * (`EmitRuntimeEvent`) ships. Same TODO is duplicated at every callsite so a
 * future grep finds them all at once.
 */
async function emitRuntimeEvent(
  signalr: SignalRClient,
  type: string,
  body: Record<string, unknown>,
): Promise<void> {
  // Runtime-scope events have no session — sessionId stays empty and the hub
  // routes by runtimeId from the connection. We project them as Status frames
  // with the body folded into `eventData` JSON; the cursor-native chat UX
  // ignores Status without a runStatus column, but the audit row keeps the
  // body for observability.
  const payload: EmitEventPayload = {
    sessionId: '',
    kind: AgentEventKind.Status,
    eventData: JSON.stringify({ type, ...body }),
    emittedAt: new Date().toISOString(),
  }
  try {
    await signalr.emitEvent(payload)
  } catch {
    // Best-effort — swallow.
  }
}

// ============================================================================
// CLI entry point
// ============================================================================
//
// Only run runMain when this file is executed directly (i.e. `node dist/main.js`).
// Importing it from tests does NOT trigger the runtime path — tests call
// `runMain(overrides)` themselves with whatever fake graph they need.

if (import.meta.url === `file://${process.argv[1]}`) {
  // === Crash capture ===
  //
  // Before today, the daemon had no `uncaughtException` / `unhandledRejection`
  // handlers. Combined with the `unref()`'d heartbeat timer, that meant any
  // async throw escaping a SignalR / SDK / child-process callback killed the
  // process silently — Fly stderr buffering swallowed the default Node print,
  // RuntimeErrorReports stayed empty, and the heartbeat watcher only saw the
  // missed pulse 15–18s later (terminal `Failed` after 3 respawns).
  //
  // The cursor SDK in particular spawns a native `cursorsandbox` child process
  // that emits `error` events on arch mismatch / spawn failure, and the SDK
  // itself can reject async on billing / auth / rate-limit responses from the
  // Cursor API. Any of those reaching Node's top-level today = silent death.
  //
  // These handlers print a structured `DAEMON_CRASH` line to stderr (Fly logs
  // pick it up unbuffered via `process.stderr.write`) and then exit(1) so
  // supervisord / Fly respawn matching the prior fatal path. No state changes
  // beyond logging — this only converts silent crashes into loud ones.
  const buildCrashPayload = (
    kind: 'uncaughtException' | 'unhandledRejection',
    err: unknown,
  ): {
    line: string
    category: string
    message: string
    stack: string | undefined
    context: string
  } => {
    const errObj = err instanceof Error ? err : new Error(String(err))
    const errAsRecord = errObj as unknown as Record<string, unknown>
    const decorators = {
      status: errAsRecord['status'],
      statusCode: errAsRecord['statusCode'],
      code: errAsRecord['code'],
      type: errAsRecord['type'],
      cause: errAsRecord['cause'] instanceof Error
        ? (errAsRecord['cause'] as Error).message
        : errAsRecord['cause'],
    }
    const structured = {
      level: 60, // pino fatal
      time: Date.now(),
      msg: 'DAEMON_CRASH',
      kind,
      errorName: errObj.name,
      errorMessage: errObj.message,
      errorStack: errObj.stack,
      ...decorators,
    }
    return {
      line: JSON.stringify(structured) + '\n',
      category: `daemon_crash:${kind}`,
      message: `${errObj.name}: ${errObj.message}`.slice(0, 4000),
      stack: errObj.stack?.slice(0, 16000),
      context: JSON.stringify(decorators).slice(0, 16000),
    }
  }

  const shipCrashReport = async (
    kind: 'uncaughtException' | 'unhandledRejection',
    err: unknown,
  ): Promise<void> => {
    const p = buildCrashPayload(kind, err)
    // Always write to stderr first — that's the fallback if SignalR is dead.
    try {
      process.stderr.write(p.line)
    } catch {
      // Best-effort — if stderr is itself broken (EPIPE), at least we tried.
      // eslint-disable-next-line no-console -- absolute last resort
      console.error('DAEMON_CRASH', kind, err)
    }
    // Then try the SignalR error-report channel so the trace lands in
    // RuntimeErrorReports even when Fly logs are unreadable from our side.
    // Bounded with a 2s race so a wedged connection doesn't block exit.
    const signalr = crashReporterSignalR
    if (signalr !== undefined) {
      try {
        await Promise.race([
          signalr.sendErrorReport({
            category: p.category,
            message: p.message,
            stackTrace: p.stack,
            context: p.context,
          }),
          new Promise<void>((resolve) => setTimeout(resolve, 2000)),
        ])
      } catch (reportErr) {
        crashReporterLogger?.error({ err: reportErr }, 'crash report ship failed')
      }
    }
  }

  process.on('uncaughtException', (err) => {
    void shipCrashReport('uncaughtException', err).finally(() => {
      // Defer exit one tick so the stderr write actually flushes before Node
      // tears down. Fly's log capture has dropped messages on same-tick exits.
      setImmediate(() => process.exit(1))
    })
  })

  process.on('unhandledRejection', (reason, promise) => {
    if (isBenignAbortRejection(reason)) {
      // Mark the orphaned promise as handled so Node doesn't keep yelling
      // about it. We still log a single structured warn line so this stays
      // visible in operator logs / debug-panel — it's not an error, but
      // it's worth knowing the path fired (high frequency = SDK upgrade
      // candidate).
      promise.catch(() => {
        /* intentionally drain — re-attaching after the fact tells Node
         * "we're aware of this, stop nagging". */
      })
      const errObj = reason instanceof Error ? reason : new Error(String(reason))
      const line =
        JSON.stringify({
          level: 40, // pino warn
          time: Date.now(),
          msg: 'daemon: suppressed benign abort rejection',
          errorName: errObj.name,
          errorMessage: errObj.message,
        }) + '\n'
      try {
        process.stderr.write(line)
      } catch {
        // best-effort — same justification as shipCrashReport's stderr write.
      }
      return
    }
    void shipCrashReport('unhandledRejection', reason).finally(() => {
      setImmediate(() => process.exit(1))
    })
  })

  runMain().catch((err) => {
    // eslint-disable-next-line no-console -- last-resort fatal path
    console.error('daemon: fatal:', err)
    process.exit(1)
  })
}
