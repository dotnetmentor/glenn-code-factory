// Tests for runMain — the daemon's composition root.
//
// We don't `vi.mock` any module; instead we drive the runMain dependency-
// injection seam by passing factory overrides on `MainDeps`. Each fake is a
// hand-rolled stub that exposes only the surface runMain reaches for, plus
// a shared `order` array so we can assert step ordering across stubs.

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  BootstrapAbortedError,
  type BootstrapOrchestrator,
} from './bootstrap/BootstrapOrchestrator.js'
import { DaemonConfig } from './config/DaemonConfig.js'
import type { DiskMonitor } from './disk/DiskMonitor.js'
import type { EnvVarManager } from './env/EnvVarManager.js'
import type { McpRegistry } from './mcp/McpRegistry.js'
import type { DestructiveOpGate } from './git/DestructiveOpGate.js'
import type { GitModule } from './git/GitModule.js'
import type { GitRunner } from './git/GitRunner.js'
import type { PushRetryJob } from './git/PushRetryJob.js'
import type { SshKeyHandler } from './git/SshKeyHandler.js'
import type { HeartbeatModule } from './heartbeat/HeartbeatModule.js'
import type { FileChangeWatcher } from './hooks/FileChangeWatcher.js'
import type { HookEventEmitter } from './hooks/HookEventEmitter.js'
import type { HookExecutor } from './hooks/HookExecutor.js'
import type { HookConfig, HooksModule } from './hooks/HooksModule.js'
import type { SelfHealCoordinator } from './hooks/SelfHealCoordinator.js'
import type { ShutdownCoordinator } from './lifecycle/ShutdownCoordinator.js'
import { TestRuntimeEventEmitter } from './events/RuntimeEventEmitter.js'
import { runMain, type MainDeps } from './main.js'
import type { IExecutor } from './runtime/IExecutor.js'
import type { RuntimeSpecApplier } from './runtime/RuntimeSpecApplier.js'
import type { ServiceStatusPoller } from './runtime/ServiceStatusPoller.js'
import type { SupervisordController } from './runtime/SupervisordController.js'
import type { SignalRClient } from './signalr/SignalRClient.js'
import type {
  ApplyRuntimeSpecDeltaPayload,
  ConfigUpdatePayload,
  EmitEventPayload,
  ExecuteDestructiveGitOpPayload,
  MergeBranchPayload,
  RestartServicePayload,
} from './signalr/types.js'
import type { QuietModeManager } from './turn/QuietModeManager.js'
import type { TurnRunner } from './turn/TurnRunner.js'
import type { CustomTool, ToolContext } from './turn/types.js'
import type { DaemonToolsMcpServer } from './mcp/DaemonToolsMcpServer.js'

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'
const NEW_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJyb3RhdGVkIn0.bmV3LXNpZ25hdHVyZS1mb3Itcm90YXRlZA'

function makeConfig(): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
  })
}

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log
}

interface BuiltFakes {
  order: string[]
  config: DaemonConfig
  log: ReturnType<typeof makeLogger>
  signalr: {
    stub: SignalRClient
    start: ReturnType<typeof vi.fn>
    emitEvent: ReturnType<typeof vi.fn>
    sendErrorReport: ReturnType<typeof vi.fn>
    onUpdateConfig: ReturnType<typeof vi.fn>
    onRestartService: ReturnType<typeof vi.fn>
    onExecuteDestructiveGitOp: ReturnType<typeof vi.fn>
    onMergeBranch: ReturnType<typeof vi.fn>
    onApplyRuntimeSpecDelta: ReturnType<typeof vi.fn>
    onPermissionResolved: ReturnType<typeof vi.fn>
    invokePermissionRequested: ReturnType<typeof vi.fn>
    handlers: {
      updateConfig: ((p: ConfigUpdatePayload) => void | Promise<void>) | null
      restartService: ((p: RestartServicePayload) => void | Promise<void>) | null
      executeDestructiveGitOp:
        | ((p: ExecuteDestructiveGitOpPayload) => void | Promise<void>)
        | null
      mergeBranch: ((p: MergeBranchPayload) => void | Promise<void>) | null
      applyRuntimeSpecDelta:
        | ((p: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>)
        | null
    }
  }
  bootstrap: {
    stub: BootstrapOrchestrator
    start: ReturnType<typeof vi.fn>
  }
  disk: {
    stub: DiskMonitor
    start: ReturnType<typeof vi.fn>
  }
  turnRunner: {
    stub: TurnRunner
    start: ReturnType<typeof vi.fn>
  }
  daemonToolsMcpServer: {
    stub: DaemonToolsMcpServer
    start: ReturnType<typeof vi.fn>
  }
  heartbeat: {
    stub: HeartbeatModule
    start: ReturnType<typeof vi.fn>
  }
  selfWatchdog: {
    stub: import('./heartbeat/SelfWatchdog.js').SelfWatchdog
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
  }
  livenessWorker: {
    stub: import('./heartbeat/LivenessWorker.js').LivenessWorker
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
    rotateToken: ReturnType<typeof vi.fn>
  }
  quiet: {
    stub: QuietModeManager
    start: ReturnType<typeof vi.fn>
  }
  shutdownCoord: {
    stub: ShutdownCoordinator
    install: ReturnType<typeof vi.fn>
    cancelInFlightBootstrap: (() => void) | null
  }
  customTools: CustomTool[]
  restartTool: { run: ReturnType<typeof vi.fn>; tool: CustomTool }
  hooks: {
    executor: HookExecutor
    module: {
      stub: HooksModule
      run: ReturnType<typeof vi.fn>
      setConfig: ReturnType<typeof vi.fn>
      setKillSwitch: ReturnType<typeof vi.fn>
    }
    fileWatcher: {
      stub: FileChangeWatcher
      start: ReturnType<typeof vi.fn>
      setHooks: ReturnType<typeof vi.fn>
      emitter: EventEmitter
    }
    eventEmitter: {
      stub: HookEventEmitter
      emitLifecycle: ReturnType<typeof vi.fn>
    }
    selfHeal: {
      stub: SelfHealCoordinator
      requestContinuation: ReturnType<typeof vi.fn>
    }
  }
  git: {
    sshKeyHandler: {
      stub: SshKeyHandler
      applyConfig: ReturnType<typeof vi.fn>
    }
    runner: GitRunner
    module: {
      stub: GitModule
      commit: ReturnType<typeof vi.fn>
      push: ReturnType<typeof vi.fn>
      currentBranch: ReturnType<typeof vi.fn>
      setAutoCommit: ReturnType<typeof vi.fn>
      isAutoCommit: ReturnType<typeof vi.fn>
    }
    pushRetryJob: {
      stub: PushRetryJob
      start: ReturnType<typeof vi.fn>
      stop: ReturnType<typeof vi.fn>
      recordFailure: ReturnType<typeof vi.fn>
    }
    destructiveOpGate: {
      stub: DestructiveOpGate
      handleExecuteApproved: ReturnType<typeof vi.fn>
      handleMergeBranch: ReturnType<typeof vi.fn>
      shutdown: ReturnType<typeof vi.fn>
    }
  }
  envVarManager: {
    stub: EnvVarManager
    applyDelta: ReturnType<typeof vi.fn>
    loadInitial: ReturnType<typeof vi.fn>
  }
  mcpRegistry: {
    stub: McpRegistry
    loadInitial: ReturnType<typeof vi.fn>
    entries: ReturnType<typeof vi.fn>
  }
  runtimeSpec: {
    executor: IExecutor
    supervisord: SupervisordController
    applier: {
      stub: RuntimeSpecApplier
      applyDelta: ReturnType<typeof vi.fn>
    }
  }
  runtimeEventEmitter: TestRuntimeEventEmitter
  serviceStatusPoller: {
    stub: ServiceStatusPoller
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
  }
  logTailer: {
    stub: { startTail: (s: string) => void; stopTail: (s: string) => void; dispose: () => void }
    startTail: ReturnType<typeof vi.fn>
    stopTail: ReturnType<typeof vi.fn>
    dispose: ReturnType<typeof vi.fn>
  }
  exit: ReturnType<typeof vi.fn<(code: number) => void>>
  configRotateToken: ReturnType<typeof vi.spyOn>
}

interface BuildOpts {
  signalrStartFails?: boolean
  bootstrapFails?: boolean
  loadConfigThrows?: boolean
  customToolsHasRestart?: boolean
}

function buildFakes(opts: BuildOpts = {}): BuiltFakes {
  const order: string[] = []
  const config = makeConfig()
  const log = makeLogger()

  // SignalR stub — track the registered handlers.
  const updateConfigHandlers: Array<(p: ConfigUpdatePayload) => void | Promise<void>> = []
  const restartServiceHandlers: Array<(p: RestartServicePayload) => void | Promise<void>> = []
  const executeDestructiveGitOpHandlers: Array<
    (p: ExecuteDestructiveGitOpPayload) => void | Promise<void>
  > = []
  const mergeBranchHandlers: Array<(p: MergeBranchPayload) => void | Promise<void>> = []
  const applyRuntimeSpecDeltaHandlers: Array<
    (p: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>
  > = []
  const signalrFakes = {
    handlers: {
      updateConfig: null as ((p: ConfigUpdatePayload) => void | Promise<void>) | null,
      restartService: null as ((p: RestartServicePayload) => void | Promise<void>) | null,
      executeDestructiveGitOp: null as
        | ((p: ExecuteDestructiveGitOpPayload) => void | Promise<void>)
        | null,
      mergeBranch: null as ((p: MergeBranchPayload) => void | Promise<void>) | null,
      applyRuntimeSpecDelta: null as
        | ((p: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>)
        | null,
    },
  }
  const signalrStart = vi.fn(async () => {
    order.push('signalr.start')
    if (opts.signalrStartFails) throw new Error('signalr boom')
  })
  const signalrEmitEvent = vi.fn(async (_p: EmitEventPayload) => {})
  // Daemon-fatal report surface used by the bootstrap-terminal path in main.ts
  // (see Step 8 — the catch arm awaits this before exit(1) so the .NET hub
  // gets the typed audit row). Stubbed as a no-op resolving promise so the
  // standard runMain happy path doesn't see a rejection on a missing method.
  const signalrSendErrorReport = vi.fn(async (_p: unknown) => {})
  const signalrOnUpdateConfig = vi.fn(
    (h: (p: ConfigUpdatePayload) => void | Promise<void>) => {
      updateConfigHandlers.push(h)
      signalrFakes.handlers.updateConfig = h
      order.push('signalr.onUpdateConfig')
    },
  )
  const signalrOnRestartService = vi.fn(
    (h: (p: RestartServicePayload) => void | Promise<void>) => {
      restartServiceHandlers.push(h)
      signalrFakes.handlers.restartService = h
      order.push('signalr.onRestartService')
    },
  )
  const signalrOnExecuteDestructiveGitOp = vi.fn(
    (h: (p: ExecuteDestructiveGitOpPayload) => void | Promise<void>) => {
      executeDestructiveGitOpHandlers.push(h)
      signalrFakes.handlers.executeDestructiveGitOp = h
      order.push('signalr.onExecuteDestructiveGitOp')
    },
  )
  const signalrOnMergeBranch = vi.fn(
    (h: (p: MergeBranchPayload) => void | Promise<void>) => {
      mergeBranchHandlers.push(h)
      signalrFakes.handlers.mergeBranch = h
      order.push('signalr.onMergeBranch')
    },
  )
  const signalrOnApplyRuntimeSpecDelta = vi.fn(
    (h: (p: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>) => {
      applyRuntimeSpecDeltaHandlers.push(h)
      signalrFakes.handlers.applyRuntimeSpecDelta = h
      order.push('signalr.onApplyRuntimeSpecDelta')
    },
  )
  // Log-tail receiver pair (runtime-spec-v2 Phase 5). runMain binds these
  // directly to the LogTailer's startTail/stopTail; the stubs only need to
  // exist for the .onStartLogTail / .onStopLogTail calls to resolve. We
  // capture the handlers (mirroring the other receivers above) so tests can
  // exercise the round-trip if they want.
  const startLogTailHandlers: Array<(serviceName: string) => void | Promise<void>> = []
  const stopLogTailHandlers: Array<(serviceName: string) => void | Promise<void>> = []
  const signalrOnStartLogTail = vi.fn(
    (h: (serviceName: string) => void | Promise<void>) => {
      startLogTailHandlers.push(h)
      order.push('signalr.onStartLogTail')
    },
  )
  const signalrOnStopLogTail = vi.fn(
    (h: (serviceName: string) => void | Promise<void>) => {
      stopLogTailHandlers.push(h)
      order.push('signalr.onStopLogTail')
    },
  )
  // runtime-observability-super-admin — daemon-log tail receiver pair. Same
  // stub-only contract as the service-log pair; we just need the .on* methods
  // to exist so runMain's wiring lines don't throw.
  const startDaemonLogTailHandlers: Array<() => void | Promise<void>> = []
  const stopDaemonLogTailHandlers: Array<() => void | Promise<void>> = []
  const signalrOnStartDaemonLogTail = vi.fn((h: () => void | Promise<void>) => {
    startDaemonLogTailHandlers.push(h)
    order.push('signalr.onStartDaemonLogTail')
  })
  const signalrOnStopDaemonLogTail = vi.fn((h: () => void | Promise<void>) => {
    stopDaemonLogTailHandlers.push(h)
    order.push('signalr.onStopDaemonLogTail')
  })
  // Permission-gateway wiring (agent-sdk-permissions spec). The composition
  // root registers an `onPermissionResolved` handler that forwards into the
  // gateway. We stub the registrar so runMain doesn't crash; the test doesn't
  // care about the actual permission flow (covered by PermissionGateway.test).
  const signalrOnPermissionResolved = vi.fn(() => {
    order.push('signalr.onPermissionResolved')
  })
  // `invokePermissionRequested` is called by the gateway when an SDK
  // approval request fires. Pure stub here — no turn is actually run.
  const signalrInvokePermissionRequested = vi.fn(async () => {})
  // Phase-1 diff-view-tab receivers. runMain wires both to GitModule's
  // queue-routed read methods; the stubs only need to exist for the on*
  // calls to resolve.
  const signalrOnGetChangedFiles = vi.fn(() => {
    order.push('signalr.onGetChangedFiles')
  })
  const signalrOnGetFileDiff = vi.fn(() => {
    order.push('signalr.onGetFileDiff')
  })
  // Phase-3 (compare-base) receivers — branch-scope variants. Same stub
  // shape as the Phase-1 pair; only existence matters for the wiring tests.
  const signalrOnGetBranchChangedFiles = vi.fn(() => {
    order.push('signalr.onGetBranchChangedFiles')
  })
  const signalrOnGetBranchFileDiff = vi.fn(() => {
    order.push('signalr.onGetBranchFileDiff')
  })
  const signalrOnGetCommitRange = vi.fn(() => {
    order.push('signalr.onGetCommitRange')
  })
  const signalrOnForceRebootstrap = vi.fn(() => {
    order.push('signalr.onForceRebootstrap')
  })
  const signalrStub = {
    start: signalrStart,
    emitEvent: signalrEmitEvent,
    sendErrorReport: signalrSendErrorReport,
    onUpdateConfig: signalrOnUpdateConfig,
    onRestartService: signalrOnRestartService,
    onExecuteDestructiveGitOp: signalrOnExecuteDestructiveGitOp,
    onMergeBranch: signalrOnMergeBranch,
    onApplyRuntimeSpecDelta: signalrOnApplyRuntimeSpecDelta,
    onStartLogTail: signalrOnStartLogTail,
    onStopLogTail: signalrOnStopLogTail,
    onStartDaemonLogTail: signalrOnStartDaemonLogTail,
    onStopDaemonLogTail: signalrOnStopDaemonLogTail,
    onPermissionResolved: signalrOnPermissionResolved,
    onGetChangedFiles: signalrOnGetChangedFiles,
    onGetFileDiff: signalrOnGetFileDiff,
    onGetBranchChangedFiles: signalrOnGetBranchChangedFiles,
    onGetBranchFileDiff: signalrOnGetBranchFileDiff,
    onGetCommitRange: signalrOnGetCommitRange,
    onForceRebootstrap: signalrOnForceRebootstrap,
    invokePermissionRequested: signalrInvokePermissionRequested,
  } as unknown as SignalRClient

  // Bootstrap stub.
  const bootstrapStart = vi.fn(async (_signal: AbortSignal) => {
    order.push('bootstrap.start')
    if (opts.bootstrapFails) throw new Error('bootstrap boom')
  })
  const bootstrapStub = { start: bootstrapStart } as unknown as BootstrapOrchestrator

  // Disk stub. The DiskMonitor is an EventEmitter (`pressure` event consumed
  // by main.ts) and exposes `latest()` for the heartbeat gather. We extend
  // EventEmitter so that `disk.on('pressure', ...)` works in main.ts wiring,
  // and stub `latest()` to return null (no sample yet).
  class FakeDiskMonitor extends EventEmitter {
    start = vi.fn(() => {
      order.push('disk.start')
    })
    latest = vi.fn(() => null)
  }
  const diskStub = new FakeDiskMonitor() as unknown as DiskMonitor
  const diskStart = (diskStub as unknown as FakeDiskMonitor).start

  // TurnRunner stub — extends EventEmitter to satisfy QuietModeManager etc.
  // (here we don't actually wire to QuietModeManager because that's also a
  // stub; but keeping the EventEmitter base means we mirror reality.)
  class FakeTurnRunner extends EventEmitter {
    start = vi.fn(() => {
      order.push('turnRunner.start')
    })
    setGitModule = vi.fn()
    activeTurn: { conversationId: string; turnId: string } | null = null
    getActiveTurn = vi.fn(() => this.activeTurn)
  }
  const turnRunner = new FakeTurnRunner()

  const daemonToolsMcpServerStart = vi.fn(async () => {})
  const daemonToolsMcpServerStub = {
    start: daemonToolsMcpServerStart,
    stop: vi.fn(async () => {}),
    setTurnContext: vi.fn(),
  } as unknown as DaemonToolsMcpServer

  // Heartbeat stub.
  const heartbeatStart = vi.fn(() => {
    order.push('heartbeat.start')
  })
  const heartbeatStub = { start: heartbeatStart } as unknown as HeartbeatModule

  // SelfWatchdog stub — must NOT spawn a real worker_threads worker in
  // tests (would leak workers + risk a stalled vitest tripping SIGKILL).
  const selfWatchdogStart = vi.fn(() => {
    order.push('selfWatchdog.start')
  })
  const selfWatchdogStop = vi.fn(async () => {})
  const selfWatchdogStub = {
    start: selfWatchdogStart,
    stop: selfWatchdogStop,
  } as unknown as import('./heartbeat/SelfWatchdog.js').SelfWatchdog

  // LivenessWorker stub — same reasoning as SelfWatchdog: never spawn a
  // real worker_threads worker in tests. Also exposes `rotateToken` so
  // the UpdateConfig handler's token-rotation forwarding can be asserted.
  const livenessWorkerStart = vi.fn(() => {
    order.push('livenessWorker.start')
  })
  const livenessWorkerStop = vi.fn(async () => {})
  const livenessWorkerRotateToken = vi.fn(() => {})
  const livenessWorkerStub = {
    start: livenessWorkerStart,
    stop: livenessWorkerStop,
    rotateToken: livenessWorkerRotateToken,
  } as unknown as import('./heartbeat/LivenessWorker.js').LivenessWorker

  // Quiet stub.
  const quietStart = vi.fn(() => {
    order.push('quiet.start')
  })
  const quietStub = { start: quietStart } as unknown as QuietModeManager

  // Shutdown coordinator stub — capture the cancelInFlightBootstrap callback
  // so a SIGTERM-mid-bootstrap test could drive it.
  const shutdownFakes = {
    cancelInFlightBootstrap: null as (() => void) | null,
  }
  const shutdownInstall = vi.fn(
    (callOpts: { cancelInFlightBootstrap?: () => void } = {}) => {
      order.push('shutdown.install')
      shutdownFakes.cancelInFlightBootstrap = callOpts.cancelInFlightBootstrap ?? null
    },
  )
  const shutdownStub = { install: shutdownInstall } as unknown as ShutdownCoordinator

  // Custom tools — by default include a `restart_service` tool stub. Tests
  // that want to exercise the "tool missing" branch pass
  // `customToolsHasRestart: false`.
  const restartRun = vi.fn(async (_args: unknown, _ctx: ToolContext) => {
    return { ok: true }
  })
  const restartTool: CustomTool = {
    name: 'restart_service',
    description: 'stub',
    inputSchema: {},
    run: restartRun,
  }
  const customTools: CustomTool[] =
    opts.customToolsHasRestart === false ? [] : [restartTool]

  // Hook subsystem stubs — Card 10 of daemon-hooks-runner.
  // HookExecutor is opaque to runMain (only HooksModule reaches into it), so a
  // tag object is sufficient. HooksModule, FileChangeWatcher, HookEventEmitter
  // and SelfHealCoordinator each expose narrow surfaces — tests assert
  // via the spy fns, and the stubs are cast to their full types via `unknown`.
  const hookExecutorStub = {} as unknown as HookExecutor

  const hooksModuleRun = vi.fn(
    async (_point: string, _ctx: unknown) => ({
      ranAll: true,
      failures: [],
      feedbackTexts: [],
    }),
  )
  const hooksModuleSetConfig = vi.fn<(cfg: HookConfig) => void>()
  const hooksModuleSetKillSwitch = vi.fn<(b: boolean) => void>()
  const hooksModuleStub = {
    run: hooksModuleRun,
    setConfig: hooksModuleSetConfig,
    setKillSwitch: hooksModuleSetKillSwitch,
  } as unknown as HooksModule

  // FileChangeWatcher: extend EventEmitter so the test can fire `changeBatch`.
  class FakeFileWatcher extends EventEmitter {
    start = vi.fn(async () => {
      order.push('fileWatcher.start')
    })
    stop = vi.fn(async () => {})
    setHooks = vi.fn<(hooks: { name: string; pattern: string }[]) => void>()
  }
  const fileWatcher = new FakeFileWatcher()

  const emitLifecycle = vi.fn()
  const hookEmitterStub = {
    emitLifecycle,
    emitSelfHealStarted: vi.fn(),
    emitSelfHealMaxedOut: vi.fn(),
  } as unknown as HookEventEmitter

  const selfHealRequest = vi.fn(
    async (_args: unknown) => ({ accepted: false }),
  )
  const selfHealStub = {
    requestContinuation: selfHealRequest,
  } as unknown as SelfHealCoordinator

  // Git subsystem stubs — Card 10 of daemon-git-ops.
  const sshKeyApplyConfig = vi.fn(async (_cfg: { deployKey?: string | null }) => {})
  const sshKeyHandlerStub = {
    applyConfig: sshKeyApplyConfig,
  } as unknown as SshKeyHandler

  const gitRunnerStub = {} as unknown as GitRunner

  const gitCommit = vi.fn(async (_msg: string, _ctx?: unknown) => ({
    ok: true,
    noChanges: true,
  }))
  const gitPush = vi.fn(async (_remote: string, _branch?: string, _ctx?: unknown) => ({
    ok: true,
  }))
  const gitCurrentBranch = vi.fn(async () => 'main')
  const gitSetAutoCommit = vi.fn<(b: boolean) => void>()
  const gitIsAutoCommit = vi.fn(() => false)
  const gitHandleRunnerAudit = vi.fn()
  const gitModuleStub = {
    commit: gitCommit,
    push: gitPush,
    currentBranch: gitCurrentBranch,
    setAutoCommit: gitSetAutoCommit,
    isAutoCommit: gitIsAutoCommit,
    handleRunnerAudit: gitHandleRunnerAudit,
  } as unknown as GitModule

  const pushRetryStart = vi.fn(() => {
    order.push('pushRetryJob.start')
  })
  const pushRetryStop = vi.fn()
  const pushRetryRecordFailure = vi.fn<(remote: string, branch: string) => void>()
  const pushRetryStub = {
    start: pushRetryStart,
    stop: pushRetryStop,
    recordFailure: pushRetryRecordFailure,
  } as unknown as PushRetryJob

  const destructiveHandleApproved = vi.fn(async (_opId: string) => {})
  const destructiveHandleMerge = vi.fn(async (_p: MergeBranchPayload) => {})
  const destructiveShutdown = vi.fn()
  const destructiveOpGateStub = {
    handleExecuteApproved: destructiveHandleApproved,
    handleMergeBranch: destructiveHandleMerge,
    shutdown: destructiveShutdown,
  } as unknown as DestructiveOpGate

  // EnvVarManager stub — Spec 14 Card 7. runMain only forwards deltas via
  // applyDelta; loadInitial lands in Card 8 but we expose it on the stub now
  // so future tests can lean on the same fake shape.
  const envApplyDelta = vi.fn(async (_d: unknown) => {})
  const envLoadInitial = vi.fn(async (_e: unknown) => {})
  const envVarManagerStub = {
    applyDelta: envApplyDelta,
    loadInitial: envLoadInitial,
  } as unknown as EnvVarManager

  // McpRegistry stub — Spec 15 Card 5. runMain only constructs it and threads
  // the reference; BootstrapMcpStage calls loadInitial during bootstrap, and
  // CursorFactory reads `entries()` per turn. The stub exposes both.
  const mcpLoadInitial = vi.fn<(entries: readonly unknown[]) => void>()
  const mcpEntries = vi.fn(() => [] as readonly unknown[])
  const mcpRegistryStub = {
    loadInitial: mcpLoadInitial,
    entries: mcpEntries,
  } as unknown as McpRegistry

  // Runtime-curation stubs — Spec 16 Card 6. The executor / supervisord are
  // opaque to runMain (only RuntimeSpecApplier reaches into them), so tag
  // objects suffice. The applier itself exposes a single `applyDelta` that the
  // signalr.onApplyRuntimeSpecDelta handler calls.
  const executorStub = {} as unknown as IExecutor
  const supervisordControllerStub = {} as unknown as SupervisordController
  const runtimeApplyDelta = vi.fn(async (_p: ApplyRuntimeSpecDeltaPayload) => {})
  const runtimeSpecApplierStub = {
    applyDelta: runtimeApplyDelta,
  } as unknown as RuntimeSpecApplier

  // LogTailer stub — runtime-spec-v2 Phase 5. runMain binds the SignalR
  // onStartLogTail / onStopLogTail receivers to these, and threads the stub
  // into ShutdownCoordinator for dispose on shutdown. Tests can call
  // `signalr.handlers.startLogTail(name)` to exercise the path (though the
  // current test surface doesn't yet).
  const logTailerStartTail = vi.fn<(serviceName: string) => void>()
  const logTailerStopTail = vi.fn<(serviceName: string) => void>()
  const logTailerDispose = vi.fn<() => void>()
  const logTailerStub = {
    startTail: logTailerStartTail,
    stopTail: logTailerStopTail,
    dispose: logTailerDispose,
  }

  // RuntimeEvent emitter — TestRuntimeEventEmitter records events into
  // `events[]` for inline assertions. Singleton across the test's runMain.
  const runtimeEventEmitter = new TestRuntimeEventEmitter()

  // ServiceStatusPoller stub — track start/stop. The real poller polls
  // supervisorctl every 10s; tests don't need either tick or real state.
  const pollerStart = vi.fn(() => {
    order.push('serviceStatusPoller.start')
  })
  const pollerStop = vi.fn()
  const pollerStub = {
    start: pollerStart,
    stop: pollerStop,
  } as unknown as ServiceStatusPoller

  // Exit + config token spy.
  const exit = vi.fn<(code: number) => void>()
  const configRotateToken = vi.spyOn(config, 'rotateToken')

  return {
    order,
    config,
    log,
    signalr: {
      stub: signalrStub,
      start: signalrStart,
      emitEvent: signalrEmitEvent,
      sendErrorReport: signalrSendErrorReport,
      onUpdateConfig: signalrOnUpdateConfig,
      onRestartService: signalrOnRestartService,
      onExecuteDestructiveGitOp: signalrOnExecuteDestructiveGitOp,
      onMergeBranch: signalrOnMergeBranch,
      onApplyRuntimeSpecDelta: signalrOnApplyRuntimeSpecDelta,
      onPermissionResolved: signalrOnPermissionResolved,
      invokePermissionRequested: signalrInvokePermissionRequested,
      handlers: signalrFakes.handlers,
    },
    bootstrap: { stub: bootstrapStub, start: bootstrapStart },
    disk: { stub: diskStub, start: diskStart },
    turnRunner: {
      stub: turnRunner as unknown as TurnRunner,
      start: turnRunner.start,
    },
    daemonToolsMcpServer: {
      stub: daemonToolsMcpServerStub,
      start: daemonToolsMcpServerStart,
    },
    heartbeat: { stub: heartbeatStub, start: heartbeatStart },
    selfWatchdog: {
      stub: selfWatchdogStub,
      start: selfWatchdogStart,
      stop: selfWatchdogStop,
    },
    livenessWorker: {
      stub: livenessWorkerStub,
      start: livenessWorkerStart,
      stop: livenessWorkerStop,
      rotateToken: livenessWorkerRotateToken,
    },
    quiet: { stub: quietStub, start: quietStart },
    shutdownCoord: {
      stub: shutdownStub,
      install: shutdownInstall,
      get cancelInFlightBootstrap() {
        return shutdownFakes.cancelInFlightBootstrap
      },
    },
    customTools,
    restartTool: { run: restartRun, tool: restartTool },
    hooks: {
      executor: hookExecutorStub,
      module: {
        stub: hooksModuleStub,
        run: hooksModuleRun,
        setConfig: hooksModuleSetConfig,
        setKillSwitch: hooksModuleSetKillSwitch,
      },
      fileWatcher: {
        stub: fileWatcher as unknown as FileChangeWatcher,
        start: fileWatcher.start,
        setHooks: fileWatcher.setHooks,
        emitter: fileWatcher,
      },
      eventEmitter: { stub: hookEmitterStub, emitLifecycle },
      selfHeal: { stub: selfHealStub, requestContinuation: selfHealRequest },
    },
    git: {
      sshKeyHandler: { stub: sshKeyHandlerStub, applyConfig: sshKeyApplyConfig },
      runner: gitRunnerStub,
      module: {
        stub: gitModuleStub,
        commit: gitCommit,
        push: gitPush,
        currentBranch: gitCurrentBranch,
        setAutoCommit: gitSetAutoCommit,
        isAutoCommit: gitIsAutoCommit,
      },
      pushRetryJob: {
        stub: pushRetryStub,
        start: pushRetryStart,
        stop: pushRetryStop,
        recordFailure: pushRetryRecordFailure,
      },
      destructiveOpGate: {
        stub: destructiveOpGateStub,
        handleExecuteApproved: destructiveHandleApproved,
        handleMergeBranch: destructiveHandleMerge,
        shutdown: destructiveShutdown,
      },
    },
    envVarManager: {
      stub: envVarManagerStub,
      applyDelta: envApplyDelta,
      loadInitial: envLoadInitial,
    },
    mcpRegistry: {
      stub: mcpRegistryStub,
      loadInitial: mcpLoadInitial,
      entries: mcpEntries,
    },
    runtimeSpec: {
      executor: executorStub,
      supervisord: supervisordControllerStub,
      applier: {
        stub: runtimeSpecApplierStub,
        applyDelta: runtimeApplyDelta,
      },
    },
    runtimeEventEmitter,
    serviceStatusPoller: {
      stub: pollerStub,
      start: pollerStart,
      stop: pollerStop,
    },
    logTailer: {
      stub: logTailerStub,
      startTail: logTailerStartTail,
      stopTail: logTailerStopTail,
      dispose: logTailerDispose,
    },
    exit,
    configRotateToken,
  }
}

function buildOverrides(b: BuiltFakes, opts: BuildOpts = {}): Partial<MainDeps> {
  return {
    loadConfig: () => {
      if (opts.loadConfigThrows) throw new Error('config boom')
      return b.config
    },
    buildLogger: () => b.log as unknown as import('pino').Logger,
    buildSignalR: () => b.signalr.stub,
    buildBootstrap: () => b.bootstrap.stub,
    buildDiskMonitor: () => b.disk.stub,
    fetchToolDescription: async () => ({
      description: 'stub propose_runtime_spec description',
      inputSchema: { type: 'object' },
    }),
    buildTurnRunner: () => b.turnRunner.stub,
    buildDaemonToolsMcpServer: () => b.daemonToolsMcpServer.stub,
    buildHeartbeat: () => b.heartbeat.stub,
    buildSelfWatchdog: () => b.selfWatchdog.stub,
    buildLivenessWorker: () => b.livenessWorker.stub,
    buildQuietMode: () => b.quiet.stub,
    buildShutdownCoordinator: () => b.shutdownCoord.stub,
    buildCustomTools: () => b.customTools,
    buildHookExecutor: () => b.hooks.executor,
    buildHooksModule: () => b.hooks.module.stub,
    buildFileChangeWatcher: () => b.hooks.fileWatcher.stub,
    buildHookEventEmitter: () => b.hooks.eventEmitter.stub,
    buildSelfHealCoordinator: () => b.hooks.selfHeal.stub,
    buildSshKeyHandler: () => b.git.sshKeyHandler.stub,
    buildGitRunner: () => b.git.runner,
    buildGitModule: () => b.git.module.stub,
    buildPushRetryJob: () => b.git.pushRetryJob.stub,
    buildDestructiveOpGate: () => b.git.destructiveOpGate.stub,
    buildEnvVarManager: () => b.envVarManager.stub,
    buildMcpRegistry: () => b.mcpRegistry.stub,
    buildExecutor: () => b.runtimeSpec.executor,
    buildSupervisordController: () => b.runtimeSpec.supervisord,
    buildRuntimeSpecApplier: () => b.runtimeSpec.applier.stub,
    buildRuntimeEventEmitter: () => b.runtimeEventEmitter,
    buildLogTailer: () => b.logTailer.stub as unknown as import('./logs/LogTailer.js').LogTailer,
    buildServiceStatusPoller: () => b.serviceStatusPoller.stub,
    exit: b.exit,
  }
}

// ============================================================================
// Tests
// ============================================================================

describe('runMain', () => {
  beforeEach(() => {
    vi.useRealTimers()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('runs the Scene 1 startup sequence in the documented order', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    // Expected order:
    //   signalr.start
    //   signalr.onStartLogTail / onStopLogTail (log-tail receivers)
    //   ... server-push receivers before bootstrap ...
    //   shutdown.install
    //   turnRunner.start
    //   bootstrap.start
    //   heartbeat.start, disk.start, quiet.start
    expect(b.order).toEqual([
      'selfWatchdog.start',
      'signalr.start',
      'signalr.onStartLogTail',
      'signalr.onStopLogTail',
      'signalr.onStartDaemonLogTail',
      'signalr.onStopDaemonLogTail',
      'signalr.onUpdateConfig',
      'signalr.onExecuteDestructiveGitOp',
      'signalr.onMergeBranch',
      'signalr.onGetChangedFiles',
      'signalr.onGetFileDiff',
      'signalr.onGetBranchChangedFiles',
      'signalr.onGetBranchFileDiff',
      'signalr.onGetCommitRange',
      'signalr.onApplyRuntimeSpecDelta',
      'signalr.onForceRebootstrap',
      'signalr.onRestartService',
      'shutdown.install',
      'turnRunner.start',
      'bootstrap.start',
      'heartbeat.start',
      'livenessWorker.start',
      'disk.start',
      'quiet.start',
      'pushRetryJob.start',
      'fileWatcher.start',
      'serviceStatusPoller.start',
    ])

    // The "daemon ready" log line must be emitted.
    const sawReady = b.log.info.mock.calls.some((call) =>
      call.some((arg) => typeof arg === 'string' && arg.includes('daemon ready')),
    )
    expect(sawReady).toBe(true)

    expect(b.exit).not.toHaveBeenCalled()
  })

  it('installs SIGTERM handler BEFORE heartbeat starts', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    const installIdx = b.order.indexOf('shutdown.install')
    const heartbeatIdx = b.order.indexOf('heartbeat.start')
    expect(installIdx).toBeGreaterThanOrEqual(0)
    expect(heartbeatIdx).toBeGreaterThanOrEqual(0)
    expect(installIdx).toBeLessThan(heartbeatIdx)
  })

  it('installs SIGTERM handler BEFORE bootstrap.start (so SIGTERM mid-bootstrap unwinds)', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    const installIdx = b.order.indexOf('shutdown.install')
    const bootstrapIdx = b.order.indexOf('bootstrap.start')
    expect(installIdx).toBeGreaterThanOrEqual(0)
    expect(bootstrapIdx).toBeGreaterThanOrEqual(0)
    expect(installIdx).toBeLessThan(bootstrapIdx)
  })

  it('passes a cancelInFlightBootstrap callback into shutdown.install()', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    expect(b.shutdownCoord.install).toHaveBeenCalledTimes(1)
    expect(b.shutdownCoord.cancelInFlightBootstrap).toBeTypeOf('function')
  })

  it('exits(1) when DaemonConfig.fromEnv throws', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b, { loadConfigThrows: true }))
    expect(b.exit).toHaveBeenCalledWith(1)
    // No further wiring should have happened.
    expect(b.signalr.start).not.toHaveBeenCalled()
  })

  it('exits(1) when signalr.start fails', async () => {
    const b = buildFakes({ signalrStartFails: true })
    await runMain(buildOverrides(b, { signalrStartFails: true }))
    expect(b.exit).toHaveBeenCalledWith(1)
    expect(b.bootstrap.start).not.toHaveBeenCalled()
  })

  it('exits(1) AND emits bootstrap_failed when bootstrap rejects (non-terminal: no sendErrorReport)', async () => {
    const b = buildFakes({ bootstrapFails: true })
    await runMain(buildOverrides(b, { bootstrapFails: true }))

    expect(b.exit).toHaveBeenCalledWith(1)
    // The bootstrap_failed event should have been emitted via the AssistantText
    // carrier.
    const sawBootstrapFailed = b.signalr.emitEvent.mock.calls.some((call) => {
      const payload = call[0] as EmitEventPayload
      try {
        const data = JSON.parse(payload.eventData) as { type?: string }
        return data.type === 'bootstrap_failed'
      } catch {
        return false
      }
    })
    expect(sawBootstrapFailed).toBe(true)
    // Plain `Error` is not a BootstrapAbortedError with terminal=true, so
    // the daemon SHOULD NOT escalate via sendErrorReport. Supervisord may
    // legitimately respawn for a transient failure.
    expect(b.signalr.sendErrorReport).not.toHaveBeenCalled()
    // Heartbeat etc. should NOT have started.
    expect(b.heartbeat.start).not.toHaveBeenCalled()
  })

  it('escalates via sendErrorReport when bootstrap rejects with a TERMINAL BootstrapAbortedError', async () => {
    // recoverable:false from any stage produces this shape — same path the
    // boot-attempt-cap exhaustion exits on. main.ts pivots on `err.terminal`
    // to decide whether to call sendErrorReport so the .NET hub persists a
    // typed RuntimeErrorReport row (operator-visible audit trail) before
    // exit(1).
    const terminalErr = new BootstrapAbortedError({
      stage: 'install-runtime-binary',
      reason: 'runtime binary missing',
      attempts: 1,
      terminal: true,
    })
    const b = buildFakes({ bootstrapFails: true })
    // Swap the default plain-Error rejection for a typed terminal abort.
    b.bootstrap.start.mockImplementation(async () => {
      throw terminalErr
    })
    await runMain(buildOverrides(b, { bootstrapFails: true }))

    expect(b.exit).toHaveBeenCalledWith(1)
    expect(b.signalr.sendErrorReport).toHaveBeenCalledTimes(1)
    const reportPayload = b.signalr.sendErrorReport.mock.calls[0]![0] as {
      category: string
      message: string
      context?: string
    }
    expect(reportPayload.category).toBe('bootstrap_terminal')
    expect(reportPayload.message).toContain('install-runtime-binary')
    expect(reportPayload.message).toContain('runtime binary missing')
    // bootstrap_failed event still goes out, with `terminal: true` payload.
    const bootstrapFailedEvent = b.signalr.emitEvent.mock.calls
      .map((call) => {
        const payload = call[0] as EmitEventPayload
        try {
          return JSON.parse(payload.eventData) as { type?: string; terminal?: boolean }
        } catch {
          return null
        }
      })
      .find((d) => d?.type === 'bootstrap_failed')
    expect(bootstrapFailedEvent?.terminal).toBe(true)
  })

  it('UpdateConfig with runtimeToken triggers config.rotateToken', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    expect(b.signalr.handlers.updateConfig).not.toBeNull()
    await b.signalr.handlers.updateConfig!({
      runtimeId: RUNTIME_ID,
      version: 'v2',
      runtimeToken: NEW_TOKEN,
    })
    expect(b.configRotateToken).toHaveBeenCalledWith(NEW_TOKEN)
  })

  it('UpdateConfig without runtimeToken does NOT trigger rotation', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    await b.signalr.handlers.updateConfig!({
      runtimeId: RUNTIME_ID,
      version: 'v2',
    })
    expect(b.configRotateToken).not.toHaveBeenCalled()
  })

  it('RestartService calls the restart_service tool with the payload', async () => {
    const b = buildFakes()
    await runMain(buildOverrides(b))

    expect(b.signalr.handlers.restartService).not.toBeNull()
    await b.signalr.handlers.restartService!({
      runtimeId: RUNTIME_ID,
      serviceName: 'web',
      reason: 'hung',
      requestId: 'req-1',
    })
    expect(b.restartTool.run).toHaveBeenCalledTimes(1)
    const [args, ctx] = b.restartTool.run.mock.calls[0]!
    expect(args).toEqual({ name: 'web', reason: 'hung' })
    expect((ctx as ToolContext).turnId).toBe('req-1')
    expect((ctx as ToolContext).config).toBe(b.config)
  })

  it('RestartService is a no-op when restart_service tool is not registered', async () => {
    const b = buildFakes({ customToolsHasRestart: false })
    await runMain(buildOverrides(b, { customToolsHasRestart: false }))

    await b.signalr.handlers.restartService!({
      runtimeId: RUNTIME_ID,
      serviceName: 'web',
      reason: 'hung',
      requestId: 'req-1',
    })
    expect(b.restartTool.run).not.toHaveBeenCalled()
    // A warn was logged, but we don't pin the exact message.
    expect(b.log.warn).toHaveBeenCalled()
  })

  // ============================================================================
  // Hook subsystem (Card 10 of daemon-hooks-runner)
  // ============================================================================

  describe('hook subsystem wiring', () => {
    /** Fire `idle` and let the async listener settle. */
    async function fireIdle(
      b: BuiltFakes,
      payload: {
        conversationId: string
        turnId: string
        agentId: string
        skipHooks?: boolean
      },
    ): Promise<void> {
      const turnRunner = b.turnRunner.stub as unknown as EventEmitter
      turnRunner.emit('idle', { skipHooks: false, ...payload })
      // Two micro-flushes: hooksModule.run resolves, then selfHeal/etc.
      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()
    }

    it('idle event triggers afterPrompt run with conversation/turn context', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
      })

      expect(b.hooks.module.run).toHaveBeenCalledTimes(1)
      const [point, ctx] = b.hooks.module.run.mock.calls[0]!
      expect(point).toBe('afterPrompt')
      expect((ctx as { point: string }).point).toBe('afterPrompt')
      expect((ctx as { conversationId: string }).conversationId).toBe('c-1')
      expect((ctx as { turnId: string }).turnId).toBe('t-1')
      // Self-heal should NOT be called when no failures.
      expect(b.hooks.selfHeal.requestContinuation).not.toHaveBeenCalled()
    })

    it('skipHooks=true on idle skips afterPrompt entirely', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        skipHooks: true,
      })

      expect(b.hooks.module.run).not.toHaveBeenCalled()
      expect(b.hooks.selfHeal.requestContinuation).not.toHaveBeenCalled()
    })

    it('afterPrompt failures trigger selfHeal.requestContinuation', async () => {
      const b = buildFakes()
      b.hooks.module.run.mockResolvedValueOnce({
        ranAll: false,
        failures: [
          {
            spec: { name: 'build', cmd: 'npm run build', feedbackMode: 'on-failure' },
            result: {
              exitCode: 1,
              durationMs: 1,
              outputTail: 'compile error',
              outputHash: 'h',
              timedOut: false,
              wasConfigError: false,
              onProgressLines: [],
            },
          },
        ],
        feedbackTexts: ['compile error'],
      })
      b.hooks.selfHeal.requestContinuation.mockResolvedValueOnce({
        accepted: true,
        newTurnId: 't-2',
      })

      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
      })

      expect(b.hooks.selfHeal.requestContinuation).toHaveBeenCalledTimes(1)
      const [args] = b.hooks.selfHeal.requestContinuation.mock.calls[0]!
      const a = args as {
        conversationId: string
        turnId: string
        agentId: string
        iteration: number
        failures: { hookName: string; outputTail: string }[]
      }
      expect(a.conversationId).toBe('c-1')
      expect(a.turnId).toBe('t-1')
      expect(a.agentId).toBe('cl-1')
      expect(a.iteration).toBe(0)
      expect(a.failures).toEqual([{ hookName: 'build', outputTail: 'compile error' }])
    })

    it('selfHeal iteration increments across continuation chains', async () => {
      const b = buildFakes()
      // Every afterPrompt run produces a failure to keep the chain going.
      b.hooks.module.run.mockResolvedValue({
        ranAll: false,
        failures: [
          {
            spec: { name: 'build', cmd: 'npm run build', feedbackMode: 'on-failure' },
            result: {
              exitCode: 1,
              durationMs: 1,
              outputTail: 'fail',
              outputHash: 'h',
              timedOut: false,
              wasConfigError: false,
              onProgressLines: [],
            },
          },
        ],
        feedbackTexts: ['fail'],
      })
      // First continuation accepts → newTurnId t-2; second → t-3.
      b.hooks.selfHeal.requestContinuation
        .mockResolvedValueOnce({ accepted: true, newTurnId: 't-2' })
        .mockResolvedValueOnce({ accepted: true, newTurnId: 't-3' })
        .mockResolvedValueOnce({ accepted: false, rejectionReason: 'budgetExhausted' })

      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
      })
      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-2',
        agentId: 'cl-1',
      })
      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-3',
        agentId: 'cl-1',
      })

      const calls = b.hooks.selfHeal.requestContinuation.mock.calls
      expect(calls).toHaveLength(3)
      const iters = calls.map((c) => (c[0] as { iteration: number }).iteration)
      expect(iters).toEqual([0, 1, 2])
    })

    it('UpdateConfig with valid hooksJson hot-swaps hook config + watcher hooks', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      // setConfig + setHooks were already called once during bootstrap-seed.
      const seedCalls = b.hooks.module.setConfig.mock.calls.length
      const seedSetHooks = b.hooks.fileWatcher.setHooks.mock.calls.length

      const cfg: HookConfig = {
        beforePrompt: [],
        afterPrompt: [
          { name: 'build', cmd: 'npm run build', feedbackMode: 'on-failure' },
        ],
        onFileChange: [
          {
            name: 'lint',
            cmd: 'npm run lint',
            feedbackMode: 'always',
            pattern: '**/*.ts',
          },
        ],
        beforeCommit: [],
      }
      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        // The wire DTO doesn't yet declare hooksJson; runMain reads it
        // tolerantly. We splice it on via a cast to mirror the wire-future.
        ...({ hooksJson: JSON.stringify(cfg) } as Record<string, unknown>),
      } as ConfigUpdatePayload)

      expect(b.hooks.module.setConfig).toHaveBeenCalledTimes(seedCalls + 1)
      const lastCfg =
        b.hooks.module.setConfig.mock.calls[seedCalls]?.[0] as HookConfig
      expect(lastCfg).toEqual(cfg)
      expect(b.hooks.fileWatcher.setHooks).toHaveBeenCalledTimes(seedSetHooks + 1)
      expect(b.hooks.fileWatcher.setHooks.mock.calls[seedSetHooks]?.[0]).toEqual([
        { name: 'lint', pattern: '**/*.ts' },
      ])
    })

    it('UpdateConfig with hooksKillSwitch=true forwards to setKillSwitch', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        ...({ hooksKillSwitch: true } as Record<string, unknown>),
      } as ConfigUpdatePayload)

      expect(b.hooks.module.setKillSwitch).toHaveBeenCalledWith(true)
    })

    it('UpdateConfig with invalid hooksJson is swallowed and warns', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      const seedCalls = b.hooks.module.setConfig.mock.calls.length

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        ...({ hooksJson: '{not valid json' } as Record<string, unknown>),
      } as ConfigUpdatePayload)

      // setConfig should NOT have been called again past the bootstrap seed.
      expect(b.hooks.module.setConfig.mock.calls).toHaveLength(seedCalls)
      // A warn log was emitted somewhere.
      expect(b.log.warn).toHaveBeenCalled()
    })

    it('fileWatcher changeBatch triggers onFileChange run', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      b.hooks.fileWatcher.emitter.emit('changeBatch', {
        pattern: '**/*.ts',
        hookName: 'lint',
        changedFiles: ['src/foo.ts'],
      })
      await Promise.resolve()
      await Promise.resolve()

      expect(b.hooks.module.run).toHaveBeenCalledTimes(1)
      const [point, ctx] = b.hooks.module.run.mock.calls[0]!
      expect(point).toBe('onFileChange')
      // No active turn → ctx has no turnId / conversationId.
      expect((ctx as { turnId?: string }).turnId).toBeUndefined()
      expect((ctx as { conversationId?: string }).conversationId).toBeUndefined()
    })

    it('fileWatcher changeBatch with active turn carries turn context', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      ;(b.turnRunner.stub as unknown as {
        activeTurn: { conversationId: string; turnId: string } | null
      }).activeTurn = { conversationId: 'c-9', turnId: 't-9' }

      b.hooks.fileWatcher.emitter.emit('changeBatch', {
        pattern: '**/*.ts',
        hookName: 'lint',
        changedFiles: ['src/foo.ts'],
      })
      await Promise.resolve()
      await Promise.resolve()

      expect(b.hooks.module.run).toHaveBeenCalledTimes(1)
      const ctx = b.hooks.module.run.mock.calls[0]?.[1] as {
        conversationId?: string
        turnId?: string
      }
      expect(ctx.conversationId).toBe('c-9')
      expect(ctx.turnId).toBe('t-9')
    })
  })

  // ============================================================================
  // Git subsystem (Card 10 of daemon-git-ops)
  // ============================================================================

  describe('git subsystem wiring', () => {
    /** Fire `idle` and let the async listener settle. */
    async function fireIdle(
      b: BuiltFakes,
      payload: {
        conversationId: string
        turnId: string
        agentId: string
        skipHooks?: boolean
        userPrompt?: string
        sessionId?: string
      },
    ): Promise<void> {
      const turnRunner = b.turnRunner.stub as unknown as EventEmitter
      turnRunner.emit('idle', {
        skipHooks: false,
        userPrompt: '',
        sessionId: 's-1',
        ...payload,
      })
      // Several micro-flushes: hooksModule.run resolves, then auto-commit
      // chain (commit → push → recordFailure) settles.
      for (let i = 0; i < 8; i++) await Promise.resolve()
    }

    it('UpdateConfig with autoCommit=true forwards to gitModule.setAutoCommit', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        autoCommit: true,
      })

      expect(b.git.module.setAutoCommit).toHaveBeenCalledWith(true)
    })

    it('UpdateConfig with deployKey forwards to sshKeyHandler.applyConfig', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        deployKey: 'PEM-BODY',
      })

      expect(b.git.sshKeyHandler.applyConfig).toHaveBeenCalledWith({
        deployKey: 'PEM-BODY',
      })
    })

    it('ExecuteDestructiveGitOp delegates to destructiveOpGate.handleExecuteApproved', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      expect(b.signalr.handlers.executeDestructiveGitOp).not.toBeNull()
      await b.signalr.handlers.executeDestructiveGitOp!({
        runtimeId: RUNTIME_ID,
        opId: 'approval-42',
      })
      expect(b.git.destructiveOpGate.handleExecuteApproved).toHaveBeenCalledWith(
        'approval-42',
      )
    })

    it('ExecuteDestructiveGitOp with empty opId is a no-op (warns)', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.executeDestructiveGitOp!({
        runtimeId: RUNTIME_ID,
        opId: '',
      })
      expect(b.git.destructiveOpGate.handleExecuteApproved).not.toHaveBeenCalled()
      expect(b.log.warn).toHaveBeenCalled()
    })

    it('MergeBranch delegates to destructiveOpGate.handleMergeBranch', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      const payload: MergeBranchPayload = {
        runtimeId: RUNTIME_ID,
        sourceBranch: 'feature/x',
        targetBranch: 'main',
        requestedBy: 'user@example.com',
      }
      await b.signalr.handlers.mergeBranch!(payload)
      expect(b.git.destructiveOpGate.handleMergeBranch).toHaveBeenCalledWith(payload)
    })

    it('idle does NOT auto-commit when policy is off', async () => {
      const b = buildFakes()
      b.git.module.isAutoCommit.mockReturnValue(false)
      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        userPrompt: 'add login',
      })

      expect(b.git.module.commit).not.toHaveBeenCalled()
    })

    it('idle commits + pushes when autoCommit is enabled and there are changes', async () => {
      const b = buildFakes()
      b.git.module.isAutoCommit.mockReturnValue(true)
      b.git.module.commit.mockResolvedValueOnce({
        ok: true,
        commitSha: 'abcdef0',
        branch: 'main',
        fileCount: 2,
      })
      b.git.module.push.mockResolvedValueOnce({ ok: true })

      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        userPrompt: 'add login form',
        sessionId: 's-1',
      })

      expect(b.git.module.commit).toHaveBeenCalledTimes(1)
      const [msg, ctx] = b.git.module.commit.mock.calls[0]!
      expect(msg).toBe('chore(turn): add login form')
      expect(ctx).toEqual({ conversationId: 'c-1', turnId: 't-1' })

      expect(b.git.module.push).toHaveBeenCalledTimes(1)
      const [remote, branch] = b.git.module.push.mock.calls[0]!
      expect(remote).toBe('origin')
      expect(branch).toBe('main')
      expect(b.git.pushRetryJob.recordFailure).not.toHaveBeenCalled()

      // Chat-history trailer: a single CommitMade carrier fires (as a Status
      // event with subtype 'CommitMade' in eventData) after both the commit
      // AND the push settle, with `pushed: true` on the success path.
      const commitMadeEmits = b.signalr.emitEvent.mock.calls.filter(([p]) => {
        const env = p as { kind?: string; eventData?: string }
        if (env.kind !== 'Status' || !env.eventData) return false
        try {
          const parsed = JSON.parse(env.eventData) as { subtype?: string }
          return parsed.subtype === 'CommitMade'
        } catch {
          return false
        }
      })
      expect(commitMadeEmits).toHaveLength(1)
      const envelope = commitMadeEmits[0]![0] as { sessionId: string; eventData: string }
      expect(envelope.sessionId).toBe('s-1')
      const data = JSON.parse(envelope.eventData) as Record<string, unknown>
      expect(data).toMatchObject({
        subtype: 'CommitMade',
        commitSha: 'abcdef0',
        shortSha: 'abcdef0',
        branch: 'main',
        pushed: true,
        pushFailureReason: null,
        conversationId: 'c-1',
        turnId: 't-1',
        fileCount: 2,
      })
    })

    it('idle skips push when commit reports noChanges', async () => {
      const b = buildFakes()
      b.git.module.isAutoCommit.mockReturnValue(true)
      b.git.module.commit.mockResolvedValueOnce({ ok: true, noChanges: true })

      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        userPrompt: 'noop',
      })

      expect(b.git.module.commit).toHaveBeenCalledTimes(1)
      expect(b.git.module.push).not.toHaveBeenCalled()
      // No CommitMade trailer fires when there were no changes — there's
      // nothing to point a chat-history row at.
      const commitMadeEmits = b.signalr.emitEvent.mock.calls.filter(([p]) => {
        const env = p as { kind?: string; eventData?: string }
        if (env.kind !== 'Status' || !env.eventData) return false
        try {
          const parsed = JSON.parse(env.eventData) as { subtype?: string }
          return parsed.subtype === 'CommitMade'
        } catch {
          return false
        }
      })
      expect(commitMadeEmits).toHaveLength(0)
    })

    it('idle records push failures with pushRetryJob', async () => {
      const b = buildFakes()
      b.git.module.isAutoCommit.mockReturnValue(true)
      b.git.module.commit.mockResolvedValueOnce({
        ok: true,
        commitSha: 'def0123',
        branch: 'feature/y',
        fileCount: 1,
      })
      b.git.module.push.mockResolvedValueOnce({
        ok: false,
        outputTail: 'rejected',
        failureReason: 'Conflict',
      })

      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        userPrompt: 'feature work',
        sessionId: 's-2',
      })

      expect(b.git.pushRetryJob.recordFailure).toHaveBeenCalledWith(
        'origin',
        'feature/y',
      )

      // Failure path still emits ONE CommitMade trailer, with `pushed: false`
      // and the classifier's reason so the chat-history rust-toned variant has
      // copy to render ("Committed locally — push failed (Conflict) · …").
      const commitMadeEmits = b.signalr.emitEvent.mock.calls.filter(([p]) => {
        const env = p as { kind?: string; eventData?: string }
        if (env.kind !== 'Status' || !env.eventData) return false
        try {
          const parsed = JSON.parse(env.eventData) as { subtype?: string }
          return parsed.subtype === 'CommitMade'
        } catch {
          return false
        }
      })
      expect(commitMadeEmits).toHaveLength(1)
      const data = JSON.parse(
        (commitMadeEmits[0]![0] as { eventData: string }).eventData,
      ) as Record<string, unknown>
      expect(data).toMatchObject({
        subtype: 'CommitMade',
        commitSha: 'def0123',
        branch: 'feature/y',
        pushed: false,
        pushFailureReason: 'Conflict',
      })
    })

    it('idle with skipHooks=true skips auto-commit even when policy is on', async () => {
      const b = buildFakes()
      b.git.module.isAutoCommit.mockReturnValue(true)
      await runMain(buildOverrides(b))

      await fireIdle(b, {
        conversationId: 'c-1',
        turnId: 't-1',
        agentId: 'cl-1',
        skipHooks: true,
        userPrompt: 'whatever',
      })

      expect(b.git.module.commit).not.toHaveBeenCalled()
    })
  })

  // ============================================================================
  // Env-var delta wiring (Spec 14 Card 7)
  // ============================================================================

  describe('env-var subsystem wiring', () => {
    it('UpdateConfig with envVarsDelta forwards to envVarManager.applyDelta', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      const delta = [
        { key: 'OPENAI_API_KEY', value: 'sk-test' },
        { key: 'OBSOLETE', value: null },
      ]
      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        envVarsDelta: delta,
      })

      expect(b.envVarManager.applyDelta).toHaveBeenCalledTimes(1)
      expect(b.envVarManager.applyDelta).toHaveBeenCalledWith(delta)
    })

    it('UpdateConfig with empty envVarsDelta is a no-op', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
        envVarsDelta: [],
      })

      expect(b.envVarManager.applyDelta).not.toHaveBeenCalled()
    })

    it('UpdateConfig without envVarsDelta does not call applyDelta', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      await b.signalr.handlers.updateConfig!({
        runtimeId: RUNTIME_ID,
        version: 'v2',
      })

      expect(b.envVarManager.applyDelta).not.toHaveBeenCalled()
    })

    it('applyDelta failures are swallowed and warned (do not break UpdateConfig)', async () => {
      const b = buildFakes()
      b.envVarManager.applyDelta.mockRejectedValueOnce(new Error('disk full'))
      await runMain(buildOverrides(b))

      await expect(
        b.signalr.handlers.updateConfig!({
          runtimeId: RUNTIME_ID,
          version: 'v2',
          envVarsDelta: [{ key: 'X', value: 'y' }],
        }),
      ).resolves.toBeUndefined()

      expect(b.log.warn).toHaveBeenCalled()
    })
  })

  // ============================================================================
  // Runtime-spec applier wiring (Spec 16 Card 6)
  // ============================================================================

  describe('runtime-spec applier wiring', () => {
    it('ApplyRuntimeSpecDelta delegates to runtimeSpecApplier.applyDelta', async () => {
      const b = buildFakes()
      await runMain(buildOverrides(b))

      expect(b.signalr.handlers.applyRuntimeSpecDelta).not.toBeNull()
      const payload: ApplyRuntimeSpecDeltaPayload = {
        proposalId: 'proposal-1',
        delta: {
          newOrChangedServices: [
            { name: 'redis', command: '/usr/bin/redis-server' },
          ],
          removedServices: [],
          installChanged: false,
          setupChanged: false,
          hasChanges: true,
        },
      }
      await b.signalr.handlers.applyRuntimeSpecDelta!(payload)
      // The handler in main.ts is fire-and-forget (no await on the inner
      // promise), so flush a few microtasks before asserting.
      await Promise.resolve()
      await Promise.resolve()

      expect(b.runtimeSpec.applier.applyDelta).toHaveBeenCalledTimes(1)
      expect(b.runtimeSpec.applier.applyDelta).toHaveBeenCalledWith(payload)
    })

    it('runtimeSpecApplier.applyDelta rejection does not throw out of the handler', async () => {
      const b = buildFakes()
      b.runtimeSpec.applier.applyDelta.mockRejectedValueOnce(new Error('boom'))
      await runMain(buildOverrides(b))

      await expect(
        b.signalr.handlers.applyRuntimeSpecDelta!({
          proposalId: 'proposal-x',
          delta: {
            newOrChangedServices: [],
            removedServices: [],
            installChanged: false,
            setupChanged: false,
            hasChanges: false,
          },
        }),
      ).resolves.toBeUndefined()
      // Flush microtasks so the .catch(...) runs and logs.
      await Promise.resolve()
      await Promise.resolve()
      expect(b.log.error).toHaveBeenCalled()
    })
  })
})
