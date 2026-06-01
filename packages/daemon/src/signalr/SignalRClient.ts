// SignalRClient — the *only* module in the daemon that touches the raw
// `@microsoft/signalr` API. Other modules (HeartbeatLoop, TurnRunner,
// EventEmitter, …) get a typed surface via `client.invoke*()` / `client.on*()`
// and never see a `HubConnection` instance.
//
// **Card 2 (daemon-codegen migration).** Outbound and inbound contracts come
// from the generated TypedSignalR proxy / receiver under `../generated/signalr/`.
// We hold a typed `IRuntimeHub` proxy and dispatch every named wrapper through
// it — that means a backend rename or removal breaks compile here, instead of
// drifting silently into a runtime "binding error" deep inside SignalR. The
// previous hand-written `./types.ts` mirror is reduced to a re-export shim
// (kept for the daemon-only types like `SessionEvent` and `TurnCompletedPayload`,
// which the hub stores as raw JSON inside `EmitEvent.eventData`).
//
// Design notes:
//
//   - Concrete class, no `ISignalRClient` interface. Tests inject a fake
//     `HubConnectionBuilder` factory (`hubBuilderFactory`) instead of mocking
//     the npm package.
//
//   - The `accessTokenFactory` re-reads `config.runtimeToken` on every
//     reconnect, so token rotation needs no special wiring here: a
//     `ConfigUpdatePayload` handler calls `config.rotateToken(newToken)` and
//     the next organic reconnect picks up the new value.
//
//   - WebSockets-only with `skipNegotiation: true`. The .NET hub uses JWT
//     bearer auth and lifts `?access_token=` from the query string for the
//     WebSocket transport (see `AuthenticationExtensions.AddRuntimeTokenAuthScheme`
//     `OnMessageReceived` for `/hubs/runtime`), so the negotiate roundtrip is
//     pure overhead.
//
//   - Inbound handlers are wrapped in a runtime-id guard: when the .NET DTO
//     stamps `runtimeId` (a follow-up card on the spec will), a mismatch
//     drops the message + warns. The hub already pins each connection to one
//     runtime via groups, so this is defence-in-depth, not the primary
//     isolation mechanism.

import {
  type HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  type IHttpConnectionOptions,
  LogLevel,
} from '@microsoft/signalr'
import type { Logger } from 'pino'
// `@microsoft/signalr` does a runtime `require("ws")` to resolve the WebSocket
// implementation under Node. esbuild can't follow that dynamic require through
// our single-file bundle (it leaves the call as `require("ws")` against the
// runtime's module resolver, which then can't find ws on disk because the
// daemon ships as a single file with no node_modules). We instead import ws
// statically — esbuild bundles it — and pass the WebSocket constructor into
// `withUrl` options. SignalR sees `options.WebSocket` is already populated
// and skips the dynamic require entirely.
import WS from 'ws'

import { DaemonConfig } from '../config/DaemonConfig.js'
import { IndefiniteReconnectPolicy } from './retryPolicy.js'

// Generated typed proxy / receiver. The proxy gives us a strongly-typed view
// of the hub's outbound surface (`heartbeat`, `emitEvent`, `runtimeReady`,
// …) — calling a method that doesn't exist server-side becomes a compile
// error here, which is the whole point of Card 2.
import {
  getHubProxyFactory,
  getReceiverRegister,
} from '../generated/signalr/TypedSignalR.Client/index.js'
import type {
  IRuntimeClient,
  IRuntimeHub,
} from '../generated/signalr/TypedSignalR.Client/Source.Features.SignalR.Hubs.js'
import type {
  HeartbeatPayload as GeneratedHeartbeatPayload,
  EmitEventPayload as GeneratedEmitEventPayload,
  PermissionRequestedPayload as GeneratedPermissionRequestedPayload,
  ResolvePermissionPayload as GeneratedResolvePermissionPayload,
  ServiceLogLineDto as GeneratedServiceLogLineDto,
  DaemonLogLineDto as GeneratedDaemonLogLineDto,
  StageAttachmentPayload as GeneratedStageAttachmentPayload,
} from '../generated/signalr/Source.Features.SignalR.Contracts.js'
import type { AgentPermissionsConfig } from '../generated/signalr/Source.Features.AgentPermissions.Models.js'
import type {
  ChangedFilesRequest as GeneratedChangedFilesRequest,
  ChangedFilesResponse as GeneratedChangedFilesResponse,
  CommitRangeResponse as GeneratedCommitRangeResponse,
  FileDiffRequest as GeneratedFileDiffRequest,
  FileDiffResponse as GeneratedFileDiffResponse,
} from '../generated/signalr/Source.Features.Diffs.js'

// Shim types preserve the daemon's longstanding "wire reality" call-site
// contract (e.g. `cpuPercent: number | null` rather than `number | undefined`,
// `eventType: AgentEventType | string` rather than the strict enum). The shim
// is wire-compatible with the generated forms — the underlying JSON shape is
// identical — so the cast at each proxy boundary below is sound.
import type {
  AgentSecretsDto,
  ApplyRuntimeSpecDeltaPayload,
  BootstrapPayloadV2,
  CancelTurnPayload,
  ConfigUpdatePayload,
  DiskPressurePayload,
  EmitEventPayload,
  ErrorReportPayload,
  ExecuteDestructiveGitOpPayload,
  ForceRebootstrapPayload,
  HeartbeatPayload,
  MergeBranchPayload,
  RestartServicePayload,
  StartTurnPayload,
} from './types.js'
import { AgentEventKind } from './types.js'

/**
 * Thrown by `SignalRClient.start()` when the underlying connection cannot be
 * established. `recoverable` distinguishes transient failures (network blip,
 * server cold start) — for which the caller should retry — from terminal ones.
 */
export class SignalRConnectError extends Error {
  readonly recoverable: boolean
  constructor(opts: { reason: string; recoverable: boolean; cause?: unknown }) {
    super(opts.reason, opts.cause === undefined ? undefined : { cause: opts.cause })
    this.name = 'SignalRConnectError'
    this.recoverable = opts.recoverable
  }
}

/**
 * Factory the client uses to obtain a fresh `HubConnectionBuilder`. Production
 * code uses the default; tests pass a hand-rolled fake so we don't have to
 * `vi.mock('@microsoft/signalr')`.
 */
export type HubBuilderFactory = () => HubConnectionBuilder

const defaultHubBuilderFactory: HubBuilderFactory = () => new HubConnectionBuilder()

interface SignalRClientDeps {
  config: DaemonConfig
  logger: Logger
  hubBuilderFactory?: HubBuilderFactory
}

/**
 * Inbound handler slots. Each `on*` registrar parks the user's handler here;
 * the typed `IRuntimeClient` receiver registered in `start()` dispatches the
 * matching wire message through `#guardAndDispatch` into the slot. Slots are
 * typed against the shim-side payload (the daemon's wire-reality contract);
 * the cast happens at the receiver boundary where we know the shapes are JSON-
 * compatible.
 */
type RuntimeClientHandlers = {
  startTurn?: (payload: StartTurnPayload) => void | Promise<void>
  cancelTurn?: (payload: CancelTurnPayload) => void | Promise<void>
  applyRuntimeSpecDelta?: (payload: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>
  updateConfig?: (payload: ConfigUpdatePayload) => void | Promise<void>
  executeDestructiveGitOp?: (payload: ExecuteDestructiveGitOpPayload) => void | Promise<void>
  mergeBranch?: (payload: MergeBranchPayload) => void | Promise<void>
  restartService?: (payload: RestartServicePayload) => void | Promise<void>
  forceRebootstrap?: (payload: ForceRebootstrapPayload) => void | Promise<void>
  stageAttachment?: (payload: GeneratedStageAttachmentPayload) => void | Promise<void>
  permissionResolved?: (
    payload: GeneratedResolvePermissionPayload,
  ) => void | Promise<void>
  startLogTail?: (serviceName: string) => void | Promise<void>
  stopLogTail?: (serviceName: string) => void | Promise<void>
  /**
   * runtime-observability-super-admin — `StartDaemonLogTail` (no args). The
   * super-admin opened the runtime drawer's Daemon Logs tab; the daemon
   * should begin tailing its own `/var/log/supervisor/agent.{out,err}.log`
   * via a reference-counted LogTailer that emits each line through
   * `RuntimeHub.DaemonLogLine`. Unset until the composition root wires it.
   */
  startDaemonLogTail?: () => void | Promise<void>
  stopDaemonLogTail?: () => void | Promise<void>
  /**
   * Phase-1 diff-view-tab handler. Unlike the void slots above, this is a
   * request/response over SignalR — the server invokes via the typed client
   * proxy and awaits the daemon's reply. The handler MUST return a value
   * (or throw — the server surfaces the rejection as a 500). When unset,
   * the receiver below throws, surfacing a clear error server-side rather
   * than silently hanging until the 30s controller timeout.
   */
  getChangedFiles?: (
    runtimeId: string,
    req: GeneratedChangedFilesRequest,
  ) => Promise<GeneratedChangedFilesResponse>
  getFileDiff?: (
    runtimeId: string,
    req: GeneratedFileDiffRequest,
  ) => Promise<GeneratedFileDiffResponse>
  /**
   * Phase-3 (compare-base) handlers. Same request/response semantics as
   * `getChangedFiles`/`getFileDiff` but for branch-scope diffs (base..head),
   * driving the "compare against main" + commit-picker UX. Wired by the
   * composition root to `GitModule.getBranchChangedFiles` /
   * `getBranchFileDiff` / `getCommitRange`.
   */
  getBranchChangedFiles?: (
    runtimeId: string,
    baseRef: string,
    headRef: string,
  ) => Promise<GeneratedChangedFilesResponse>
  getBranchFileDiff?: (
    runtimeId: string,
    baseRef: string,
    headRef: string,
    path: string,
  ) => Promise<GeneratedFileDiffResponse>
  getCommitRange?: (
    runtimeId: string,
    baseRef: string,
    headRef: string,
    limit: number,
  ) => Promise<GeneratedCommitRangeResponse>
}

/**
 * Per-method ring-buffer cap for the early-arrival buffer (see `#buffers` /
 * `#bufferOrDispatch`). A misbehaving server cannot push us past this many
 * pending messages per method — on overflow we drop OLDEST with a warn log, so
 * total per-process memory is bounded independent of server behaviour.
 *
 * Sized for the realistic bootstrap race: the only documented case is one
 * `UpdateConfig` push fired by `OnConnectedAsync`; 16 gives generous headroom
 * for any future "push a small burst on connect" patterns without growing
 * unbounded.
 */
const MAX_BUFFERED_MESSAGES = 16

/**
 * Server-push (fire-and-forget) inbound methods that need early-arrival
 * buffering. These are dispatched through `#guardAndDispatch`, which would
 * otherwise silently drop the payload when the handler hasn't been wired in
 * yet (the original bootstrap-UpdateConfig race).
 *
 * Request/response methods (`GetChangedFiles`, `GetFileDiff`, …) are NOT in
 * this list: their unset-handler path throws synchronously so the server sees
 * a clear failure, and buffering them would mean answering with a stale value
 * later when no one is listening for the reply. Keep them strict.
 */
type BufferableMethod =
  | 'StartTurn'
  | 'CancelTurn'
  | 'ApplyRuntimeSpecDelta'
  | 'UpdateConfig'
  | 'ExecuteDestructiveGitOp'
  | 'MergeBranch'
  | 'RestartService'
  | 'ForceRebootstrap'
  | 'StageAttachment'
  | 'PermissionResolved'
  | 'StartLogTail'
  | 'StopLogTail'
  | 'StartDaemonLogTail'
  | 'StopDaemonLogTail'

export class SignalRClient {
  readonly #config: DaemonConfig
  readonly #logger: Logger
  readonly #hubBuilderFactory: HubBuilderFactory

  #connection: HubConnection | null = null
  #proxy: IRuntimeHub | null = null
  #handlers: RuntimeClientHandlers = {}
  #connectedListeners: Array<() => void> = []
  #disconnectedListeners: Array<(error?: Error) => void> = []

  /**
   * Early-arrival buffer for server-push messages that land BEFORE the daemon
   * wires its handler. The canonical case is the bootstrap `UpdateConfig`
   * pushed by `RuntimeHub.OnConnectedAsync` immediately after handshake —
   * `await signalr.start()` returns, the event loop yields on the next
   * `await` in main.ts, the push runs, finds `#handlers.updateConfig ===
   * undefined`, and used to be silently dropped. Auto-commit then never fired
   * for the rest of the process lifetime.
   *
   * Contract:
   *   - Per-method ring buffer of `MAX_BUFFERED_MESSAGES`; drop-oldest on
   *     overflow with a warn log (bounded memory).
   *   - When `on*(handler)` registers FIRST, the buffer for that method is
   *     drained into the handler exactly ONCE and then cleared. A subsequent
   *     `on*(handler2)` call sees an empty buffer and does NOT receive the
   *     old messages — that's a fresh subscription, by design.
   *   - Replay scheduling: the live dispatch path in `#guardAndDispatch` is
   *     async (it `await`s the handler). For consistency, replay is also
   *     async-sequential but the `on*()` registrar returns synchronously
   *     (fire-and-forget the drain) — that matches how live messages flow
   *     through the receiver method (which returns the in-flight promise to
   *     SignalR's dispatch machinery, not to the registrar).
   *   - Only fire-and-forget server-push methods are buffered (see
   *     `BufferableMethod`); request/response methods stay strict.
   */
  #buffers: Map<BufferableMethod, unknown[]> = new Map()

  constructor(deps: SignalRClientDeps) {
    this.#config = deps.config
    this.#logger = deps.logger.child({ module: 'signalr' })
    this.#hubBuilderFactory = deps.hubBuilderFactory ?? defaultHubBuilderFactory
  }

  // ============================================================================
  // Lifecycle
  // ============================================================================

  async start(): Promise<void> {
    if (this.#connection) {
      throw new Error('SignalRClient.start called twice')
    }

    const url = new URL('/hubs/runtime', this.#config.mainApiUrl).toString()

    // SignalR's HttpConnection inspects `options.WebSocket` at runtime and, if
    // present, uses it instead of doing the dynamic `require("ws")` we can't
    // satisfy from a single-file bundle (see import block above). The public
    // `IHttpConnectionOptions` type doesn't surface that field, so we widen
    // locally rather than `as any` — the rest of the options stay strongly
    // typed and only the one field SignalR documents-by-source-code is loose.
    const connOptions: IHttpConnectionOptions & { WebSocket?: unknown } = {
      // Re-read on every (re)connect — token rotation just works.
      accessTokenFactory: () => this.#config.runtimeToken,
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      WebSocket: WS,
    }

    const conn = this.#hubBuilderFactory()
      .withUrl(url, connOptions)
      .withAutomaticReconnect(new IndefiniteReconnectPolicy())
      .configureLogging(LogLevel.Information)
      .build()

    conn.onreconnecting((err) => {
      this.#logger.warn({ err }, 'signalr reconnecting')
    })
    conn.onreconnected((id) => {
      this.#logger.info({ connectionId: id }, 'signalr reconnected')
      for (const cb of this.#connectedListeners) cb()
    })
    conn.onclose((err) => {
      this.#logger.warn({ err }, 'signalr connection closed')
      for (const cb of this.#disconnectedListeners) cb(err)
    })

    this.#connection = conn
    this.#proxy = getHubProxyFactory('IRuntimeHub').createHubProxy(conn)

    // Wire the typed inbound receiver. Each generated `IRuntimeClient` method
    // is dispatched to the matching slot in `#handlers` (set by `on*`
    // wrappers below) — unset slots are silent. Wrapping in
    // `#guardAndDispatch` gives us the runtime-id guard + handler-exception
    // isolation in one place. The casts at each method boundary translate the
    // generated wire-strict shape to the daemon's wire-reality shim shape;
    // the underlying JSON is identical (see types.ts for the loosening
    // rationale).
    const receiver: IRuntimeClient = {
      startTurn: async (p) =>
        this.#guardAndDispatch(
          'StartTurn',
          p as unknown as StartTurnPayload,
          this.#handlers.startTurn,
        ),
      cancelTurn: async (p) =>
        this.#guardAndDispatch(
          'CancelTurn',
          p as unknown as CancelTurnPayload,
          this.#handlers.cancelTurn,
        ),
      applyRuntimeSpecDelta: async (p) =>
        this.#guardAndDispatch(
          'ApplyRuntimeSpecDelta',
          p as unknown as ApplyRuntimeSpecDeltaPayload,
          this.#handlers.applyRuntimeSpecDelta,
        ),
      updateConfig: async (p) =>
        this.#guardAndDispatch(
          'UpdateConfig',
          p as unknown as ConfigUpdatePayload,
          this.#handlers.updateConfig,
        ),
      executeDestructiveGitOp: async (opId) =>
        // The wire payload is a bare string today (.NET sends `opId` as a
        // top-level Guid). We adapt to the daemon's longstanding
        // `ExecuteDestructiveGitOpPayload` shape `{ opId, runtimeId? }` here
        // so the registered handler doesn't have to.
        this.#guardAndDispatch(
          'ExecuteDestructiveGitOp',
          { opId } as ExecuteDestructiveGitOpPayload,
          this.#handlers.executeDestructiveGitOp,
        ),
      mergeBranch: async (p) =>
        this.#guardAndDispatch(
          'MergeBranch',
          p as unknown as MergeBranchPayload,
          this.#handlers.mergeBranch,
        ),
      restartService: async (p) =>
        this.#guardAndDispatch(
          'RestartService',
          p as unknown as RestartServicePayload,
          this.#handlers.restartService,
        ),
      forceRebootstrap: async (p) =>
        this.#guardAndDispatch(
          'ForceRebootstrap',
          p as unknown as ForceRebootstrapPayload,
          this.#handlers.forceRebootstrap,
        ),
      stageAttachment: async (p) =>
        this.#guardAndDispatch(
          'StageAttachment',
          p as unknown as GeneratedStageAttachmentPayload,
          this.#handlers.stageAttachment,
        ),
      permissionResolved: async (p) =>
        this.#guardAndDispatch(
          'PermissionResolved',
          p,
          this.#handlers.permissionResolved,
        ),
      startLogTail: async (serviceName) =>
        // Wire payload is a bare string — wrap it for the guard's
        // "is object?" check (it short-circuits on non-objects which is
        // exactly the right shape for a primitive payload).
        this.#guardAndDispatch(
          'StartLogTail',
          serviceName,
          this.#handlers.startLogTail,
        ),
      stopLogTail: async (serviceName) =>
        this.#guardAndDispatch(
          'StopLogTail',
          serviceName,
          this.#handlers.stopLogTail,
        ),
      startDaemonLogTail: async () => {
        // No-arg push. Routed through `#guardAndDispatch` so the bootstrap-
        // race buffering applies uniformly to every fire-and-forget server
        // push (see `BufferableMethod`). `undefined` payload is benign — the
        // runtime-id guard short-circuits on non-objects, and the replay
        // path will invoke the eventual handler with `undefined` exactly as
        // SignalR would deliver a no-arg push live.
        this.#guardAndDispatch<undefined>(
          'StartDaemonLogTail',
          undefined,
          this.#handlers.startDaemonLogTail as
            | ((payload: undefined) => void | Promise<void>)
            | undefined,
        )
      },
      stopDaemonLogTail: async () => {
        this.#guardAndDispatch<undefined>(
          'StopDaemonLogTail',
          undefined,
          this.#handlers.stopDaemonLogTail as
            | ((payload: undefined) => void | Promise<void>)
            | undefined,
        )
      },
      // Request/response slot (Phase-1 diff-view-tab). The server INVOKES
      // this and awaits a value, so we deliberately don't run through
      // `#guardAndDispatch` (which is fire-and-forget for void handlers and
      // never returns a value). Instead we throw on missing handler so the
      // server surfaces a clear failure rather than waiting on the controller
      // timeout.
      getChangedFiles: async (runtimeId, req) => {
        const handler = this.#handlers.getChangedFiles
        if (!handler) {
          throw new Error('GetChangedFiles handler not registered on the daemon')
        }
        return handler(runtimeId, req)
      },
      getFileDiff: async (runtimeId, req) => {
        const handler = this.#handlers.getFileDiff
        if (!handler) {
          throw new Error('GetFileDiff handler not registered on the daemon')
        }
        return handler(runtimeId, req)
      },
      getBranchChangedFiles: async (runtimeId, baseRef, headRef) => {
        const handler = this.#handlers.getBranchChangedFiles
        if (!handler) {
          throw new Error('GetBranchChangedFiles handler not registered on the daemon')
        }
        return handler(runtimeId, baseRef, headRef)
      },
      getBranchFileDiff: async (runtimeId, baseRef, headRef, path) => {
        const handler = this.#handlers.getBranchFileDiff
        if (!handler) {
          throw new Error('GetBranchFileDiff handler not registered on the daemon')
        }
        return handler(runtimeId, baseRef, headRef, path)
      },
      getCommitRange: async (runtimeId, baseRef, headRef, limit) => {
        const handler = this.#handlers.getCommitRange
        if (!handler) {
          throw new Error('GetCommitRange handler not registered on the daemon')
        }
        return handler(runtimeId, baseRef, headRef, limit)
      },
    }
    getReceiverRegister('IRuntimeClient').register(conn, receiver)

    try {
      await conn.start()
    } catch (cause) {
      // Initial-connect failures are usually transient (DNS, server cold
      // start, brief network partition). The supervisor / orchestrator
      // decides whether to retry; we surface `recoverable: true` to make that
      // explicit. We deliberately do NOT clear `#connection` here — once a
      // builder has produced a HubConnection, leaving it in place would let
      // a follow-up `start()` retry without rebuilding. Today we throw to
      // surface the failure; the caller restarts the daemon process, which
      // gives a fresh `SignalRClient` instance.
      throw new SignalRConnectError({
        reason: cause instanceof Error ? cause.message : String(cause),
        recoverable: true,
        cause,
      })
    }

    this.#logger.info({ url }, 'signalr connected')
    for (const cb of this.#connectedListeners) cb()
  }

  async stop(): Promise<void> {
    if (!this.#connection) return
    if (this.#connection.state === HubConnectionState.Disconnected) return
    await this.#connection.stop()
  }

  // ============================================================================
  // Outbound (typed) — daemon → hub
  //
  // Every wrapper below dispatches through the generated `IRuntimeHub` proxy.
  // Adding a new outbound method on the .NET side regenerates the proxy and
  // surfaces here for free; removing one breaks compile, which is the point.
  // ============================================================================

  async sendHeartbeat(payload: HeartbeatPayload): Promise<void> {
    await this.#requireProxy().heartbeat(payload as unknown as GeneratedHeartbeatPayload)
  }

  /**
   * Daemon-to-server disk-pressure transition (Phase D Card 3). Pushed by the
   * disk-monitor wiring in main.ts when DiskMonitor emits a level change.
   * Server persists a RuntimeDiskPressureEvent row + fans out to the project
   * group via IAgentClient.RuntimeDiskPressure.
   */
  async reportDiskPressure(payload: DiskPressurePayload): Promise<void> {
    await this.#requireProxy().reportDiskPressure(payload)
  }

  async emitEvent(payload: EmitEventPayload): Promise<void> {
    // Pass-through. Card 3.5/9 removed the wire-boundary translator: every
    // caller now sets `kind: AgentEventKind` directly. The shape on the wire
    // matches the .NET `EmitEventPayload` DTO 1:1.
    await this.#requireProxy().emitEvent(payload as unknown as GeneratedEmitEventPayload)
  }

  /**
   * Daemon-to-server: ship one supervised-service log line to the hub. The
   * hub broadcasts to the per-service group; subscribed React clients
   * render the line in the Logs tab. Not persisted server-side — see
   * `LogTailer` for the daemon-side mechanics.
   *
   * Hub method: `IRuntimeHub.ServiceLogLine(payload)`.
   */
  async sendServiceLogLine(payload: GeneratedServiceLogLineDto): Promise<void> {
    await this.#requireProxy().serviceLogLine(payload)
  }

  /**
   * runtime-observability-super-admin — daemon-to-server: ship one
   * daemon-log line (from the daemon's own stdout/stderr) to the hub. The
   * hub broadcasts to the `daemon-logs:{runtimeId}` group; subscribed
   * React clients render the line in the super-admin Daemon Logs tab.
   * Not persisted server-side — the disk file is the durable copy.
   *
   * Hub method: `IRuntimeHub.DaemonLogLine(payload)`.
   */
  async sendDaemonLogLine(payload: GeneratedDaemonLogLineDto): Promise<void> {
    await this.#requireProxy().daemonLogLine(payload)
  }

  /**
   * Generic typed invoke for hub methods that don't (yet) have a dedicated
   * wrapper above. Used by HookEventEmitter / GitModule / RuntimeSpecApplier
   * / TurnRunner-refusal for a long tail of methods (HookStarted, HookProgress,
   * GitOperationStarted, RuntimeSpecDeltaApplied, TurnRefused, …) — the typed
   * `IRuntimeHub` proxy covers all of them, but each consumer uses the method
   * name as a key (see the per-event tables in HookEventEmitter / GitModule),
   * so the string form is genuinely the right shape for those callers.
   *
   * Prefer the typed wrappers (`sendHeartbeat`, `emitEvent`, …) for stable
   * methods that know their name at compile time. This escape hatch exists for
   * dispatch tables.
   */
  async invoke<T = unknown>(method: string, ...args: unknown[]): Promise<T> {
    return (await this.#requireConnection().invoke<T>(method, ...args)) as T
  }

  /**
   * Fetch the full bootstrap payload from main API. Called exactly once per
   * daemon boot by `FetchingStage`; subsequent stages drive their work off the
   * returned `BootstrapPayloadV2`. Hub method: `IRuntimeHub.GetBootstrap`.
   *
   * <b>V2 cutover (P1 wiring card 32b0481b):</b> the hub now returns
   * `BootstrapPayloadV2` carrying a freeform `RuntimeSpecV2` (install bash,
   * services[], setup bash). The generated proxy types reflect this; the
   * cast at the boundary normalises to the daemon's local wire-accurate
   * `BootstrapPayloadV2` shim (which loosens `hooks` / `repo` to explicit
   * `null` matching the JSON wire reality).
   */
  async getBootstrap(): Promise<BootstrapPayloadV2> {
    const fromProxy = await this.#requireProxy().getBootstrap()
    return fromProxy as unknown as BootstrapPayloadV2
  }

  /**
   * Fetches per-turn Cursor credentials from the hub (BYOK pipeline). The hub
   * resolves the project from the daemon's runtime JWT claims and returns the
   * Cursor API key from per-project encrypted columns or — as a fallback —
   * host env vars. The field is independently nullable; no source guarantees
   * a value.
   *
   * Plaintext value; **never log it**. Callers should log only a boolean
   * presence flag (`hasCursorKey=<bool>`).
   *
   * Hub method: `IRuntimeHub.GetSecrets()` — no parameters; returns
   * `AgentSecretsDto`.
   */
  async getSecrets(): Promise<AgentSecretsDto> {
    const fromProxy = await this.#requireProxy().getSecrets()
    // Generated DTO uses optional `?: string`; the daemon's shim uses
    // `string | null` to match the JSON wire reality. Normalise to `null`
    // here so callers can rely on the nullable check.
    return {
      cursorApiKey: fromProxy.cursorApiKey ?? null,
    }
  }

  /**
   * Fetches the effective permission config for the project this daemon's
   * runtime is pinned to. The hub resolves "project override or system
   * defaults" (no merging) — see `IAgentPermissionsResolver` on the server.
   *
   * Called once per turn by `TurnRunner` just before assembling SDK options.
   * The daemon does NOT cache across turns: a mid-session config change (super
   * admin tweaks the default, project owner toggles override) needs to land on
   * the next turn without restart. Within a single turn the resolved config is
   * held in scope by the caller; the gateway uses it to decide which tool
   * approval prompts the SDK will surface.
   *
   * Hub method: `IRuntimeHub.GetAgentPermissions()` — no parameters; returns
   * `AgentPermissionsConfig`.
   */
  async getAgentPermissions(): Promise<AgentPermissionsConfig> {
    return await this.#requireProxy().getAgentPermissions()
  }

  /**
   * Forward an SDK `canUseTool` invocation up to the hub for human approval.
   * Pure relay — the hub fans the payload to every React tab in the project
   * group, and the eventual decision round-trips back via
   * `IRuntimeClient.permissionResolved` (registered on `#handlers`). The
   * `PermissionGateway` owns the correlation map; this wrapper just gets the
   * bytes onto the wire.
   *
   * Hub method: `IRuntimeHub.PermissionRequested(payload)`.
   */
  async invokePermissionRequested(
    payload: GeneratedPermissionRequestedPayload,
  ): Promise<void> {
    await this.#requireProxy().permissionRequested(payload)
  }

  /**
   * Mint a fresh short-lived GitHub App installation token for the supplied
   * repo. The hub scopes the token to the project that owns this daemon's
   * connection — passing a repoFullName that doesn't match is rejected
   * server-side, so a compromised daemon cannot mint tokens for arbitrary
   * repos in the same installation.
   *
   * `expiresAt` is normalised to a `Date` regardless of whether the wire
   * surface returns it as an ISO string (the typical JSON path) or an
   * already-parsed Date — TokenManager's cache eviction does arithmetic on
   * it, and a `string | Date` union there would just push the same
   * normalisation outward.
   *
   * Plaintext token; **never log it**. The TokenManager logs only the
   * `expiresAt` value (no token).
   *
   * Hub method: `IRuntimeHub.GetRepoAccessToken(repoFullName)` — returns
   * `RepoAccessToken { token, expiresAt }`.
   */
  async getRepoAccessToken(repoFullName: string): Promise<{ token: string; expiresAt: Date }> {
    const result = await this.#requireProxy().getRepoAccessToken(repoFullName)
    const expiresAt =
      result.expiresAt instanceof Date ? result.expiresAt : new Date(result.expiresAt)
    return { token: result.token, expiresAt }
  }

  /**
   * Per-stage progress event for the runtime-bootstrap state machine. Streamed
   * by every stage runner so main API can show live progress in the UI.
   *
   * Best-effort telemetry: a failed invoke (binding error, transient hub blip,
   * etc.) must NEVER fail the calling stage — bootstrap progress is purely
   * observability, the actual contract with main API is `RuntimeReady` at the
   * end. We swallow + log so the orchestrator keeps marching.
   *
   * TODO(runtime-bootstrap): there is no typed `ReportBootstrapProgress` hub
   * method on the .NET side yet. This wrapper invokes a generic `EmitEvent`
   * carrier with the progress payload embedded inside `eventData`. The empty
   * sessionId is the well-known "non-session" marker — the .NET binding will
   * reject it for the Guid parameter, which is exactly why we swallow the
   * rejection. Replace with a typed method that takes no sessionId once it
   * lands on the hub.
   */
  async reportBootstrapProgress(progress: {
    stage: string
    status: 'started' | 'progress' | 'completed' | 'failed' | 'skipped'
    detail?: string
  }): Promise<void> {
    const payload: EmitEventPayload = {
      sessionId: '',
      kind: AgentEventKind.Status,
      eventData: JSON.stringify({ type: 'bootstrap_progress', ...progress }),
      emittedAt: new Date().toISOString(),
    }
    try {
      // Pass-through to the hub. Card 3.5 removed the wire-boundary
      // translator that used to coerce a legacy eventType into a kind here.
      await this.emitEvent(payload)
    } catch (err) {
      // Telemetry-only — swallow so the stage doesn't fail on observability.
      this.#logger.debug({ err, progress }, 'reportBootstrapProgress invoke failed (ignored)')
    }
  }

  /**
   * Daemon-to-server fatal-error report. The hub persists this as a
   * `RuntimeErrorReport` row keyed off the connection's signed `rt_runtime`
   * claim — operator-visible audit trail for terminal failures the daemon
   * detected itself (bootstrap aborts, boot-attempt cap exceeded, …).
   *
   * Best-effort: a transient hub blip (network, server cold start) must NEVER
   * pre-empt the caller's own shutdown / exit path — we swallow + log so the
   * caller's `process.exit(1)` still runs. The category + message we hand in
   * are the only signal the operator gets after the daemon's gone; losing them
   * to a network blip is unfortunate but not worth blocking the exit on.
   *
   * Hub method: `IRuntimeHub.ReportError(payload)`.
   */
  async sendErrorReport(payload: ErrorReportPayload): Promise<void> {
    try {
      await this.#requireProxy().reportError(payload)
    } catch (err) {
      this.#logger.error({ err, category: payload.category }, 'reportError invoke failed (ignored)')
    }
  }

  /**
   * Notify main API that the runtime is fully bootstrapped and ready to serve
   * turns. Final-stage call out of `ReadyStage`.
   *
   * Calls the typed `RuntimeReady` hub method directly — no payload, the
   * hub resolves the runtime from the connection's claims. The transition this
   * triggers (Bootstrapping → Online) is the contractual edge that flips the
   * runtime live, so we let exceptions propagate up to the stage runner.
   */
  async runtimeReady(): Promise<void> {
    await this.#requireProxy().runtimeReady()
  }

  /**
   * Report the runtime's spec health after bootstrap completes
   * (self-healing-runtime-specs, card D1). `health` is `'Healthy'` when every
   * spec stage applied cleanly, `'Degraded'` when one or more NON-CRITICAL
   * (spec) stages failed and were recorded as boot issues. The runtime still
   * reached Online regardless — this is the side-channel that drives the amber
   * "spec didn't fully apply" banner + the agent self-heal loop.
   *
   * <b>Wire shape.</b> The backend's `ReportSpecHealth` hub method (added in
   * parallel by card B1) takes a single JSON-string argument — same convention
   * as `RecordRuntimeEvent` (the server's argument binder expects a `string`,
   * and passing an object trips an `InvalidDataException: Error binding
   * arguments`). We stringify the payload here at the wire boundary.
   *
   * <b>Why `invoke()` and not a typed proxy wrapper.</b> The TypedSignalR proxy
   * is regenerated from the .NET hub; `ReportSpecHealth` lands there only once
   * B1 ships + `generate-signalr.sh` runs. Until then the typed proxy has no
   * `reportSpecHealth`, so we go through the generic string-keyed `invoke()`
   * escape hatch (exactly how `RuntimeEventEmitter` calls `RecordRuntimeEvent`).
   * Swap to the typed wrapper once the proxy carries the method.
   *
   * Best-effort: this is observability/repair telemetry, never the contractual
   * Online edge (that's `runtimeReady()`). A transient hub blip must NOT undo a
   * successful boot — we swallow + log so the orchestrator keeps marching.
   */
  async reportSpecHealth(report: {
    health: 'Healthy' | 'Degraded'
    issues: ReadonlyArray<Record<string, unknown>>
    summary: string
  }): Promise<void> {
    try {
      await this.invoke('ReportSpecHealth', JSON.stringify(report))
    } catch (err) {
      this.#logger.warn(
        { err, health: report.health, issueCount: report.issues.length },
        'reportSpecHealth invoke failed (ignored)',
      )
    }
  }

  /**
   * Returns true when the underlying SignalR connection is in the `Connected`
   * state. Used by `ConnectingStage` to short-circuit out of its wait once the
   * handshake is complete.
   */
  isConnected(): boolean {
    return this.#connection?.state === HubConnectionState.Connected
  }

  // ============================================================================
  // Inbound (typed, with runtime-id guard) — hub → daemon
  //
  // Every `on*` wrapper below stores the user's handler in `#handlers`. The
  // generated `IRuntimeClient` receiver registered in `start()` dispatches
  // each inbound message into the matching slot. A rename on the .NET side
  // changes `IRuntimeClient` and breaks compile — exactly what Card 2 is for.
  // ============================================================================

  onStartTurn(handler: (payload: StartTurnPayload) => void | Promise<void>): void {
    this.#requireConnection() // preserve the legacy "must call after start()" guard
    this.#handlers.startTurn = handler
    this.#drainBuffer('StartTurn', handler)
  }

  onCancelTurn(handler: (payload: CancelTurnPayload) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.cancelTurn = handler
    this.#drainBuffer('CancelTurn', handler)
  }

  onUpdateConfig(handler: (payload: ConfigUpdatePayload) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.updateConfig = handler
    this.#drainBuffer('UpdateConfig', handler)
  }

  onApplyRuntimeSpecDelta(
    handler: (payload: ApplyRuntimeSpecDeltaPayload) => void | Promise<void>,
  ): void {
    this.#requireConnection()
    this.#handlers.applyRuntimeSpecDelta = handler
    this.#drainBuffer('ApplyRuntimeSpecDelta', handler)
  }

  onRestartService(handler: (payload: RestartServicePayload) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.restartService = handler
    this.#drainBuffer('RestartService', handler)
  }

  /**
   * Server-initiated request to run a previously-approved destructive git op.
   * The .NET DTO is just a bare `Guid opId` — we adapt it on the way through
   * so registered handlers see the daemon's longstanding
   * `ExecuteDestructiveGitOpPayload` shape `{ opId, runtimeId? }`. See Card 9
   * of daemon-git-ops.
   */
  onExecuteDestructiveGitOp(
    handler: (payload: ExecuteDestructiveGitOpPayload) => void | Promise<void>,
  ): void {
    this.#requireConnection()
    this.#handlers.executeDestructiveGitOp = handler
    this.#drainBuffer('ExecuteDestructiveGitOp', handler)
  }

  /**
   * Server-initiated branch merge. The user already approved by clicking merge
   * in the UI — the daemon delegates straight to GitModule.merge without an
   * extra approval round-trip. See Card 9 of daemon-git-ops.
   */
  onMergeBranch(handler: (payload: MergeBranchPayload) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.mergeBranch = handler
    this.#drainBuffer('MergeBranch', handler)
  }

  /**
   * Server-initiated force rebootstrap. Pushed by the .NET side when something
   * about the project changed that requires the daemon to re-pull a fresh
   * `BootstrapPayloadV2` and restart. The composition root wires this to a
   * handler that aborts any in-flight bootstrap, runs a clean shutdown, and
   * exits the process so supervisord respawns it; `FetchingStage` will then
   * pick up the new payload.
   */
  onForceRebootstrap(handler: (payload: ForceRebootstrapPayload) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.forceRebootstrap = handler
  }

  /**
   * chat-file-attachments — server pushes `StageAttachment` the moment an R2
   * upload completes. The composition root wires this to
   * `AttachmentStager.stage`, which downloads the file from the presigned URL
   * to LocalPath and acks via `RuntimeHub.ReportAttachmentStaged`. Best-effort
   * (no buffering): a push that races the runtime coming Online lands on a
   * real handler because we register before bootstrap finishes; a push to an
   * offline runtime hits an empty SignalR group and is dropped (the frontend
   * chip times out client-side per spec).
   */
  onStageAttachment(
    handler: (payload: GeneratedStageAttachmentPayload) => void | Promise<void>,
  ): void {
    this.#requireConnection()
    this.#handlers.stageAttachment = handler
    this.#drainBuffer('StageAttachment', handler)
  }

  /**
   * Server-initiated tool-permission resolution: a user has clicked one of
   * the four actions on an inline approval card (approve / approveAlwaysSession
   * / deny / denyWithFeedback). The composition root wires this to
   * `PermissionGateway.onResolution`, which looks up the pending
   * `canUseTool` waiter by `toolUseId` and resolves the SDK with the matching
   * shape. See `IRuntimeClient.permissionResolved` on the .NET hub.
   */
  onPermissionResolved(
    handler: (payload: GeneratedResolvePermissionPayload) => void | Promise<void>,
  ): void {
    this.#requireConnection()
    this.#handlers.permissionResolved = handler
    this.#drainBuffer('PermissionResolved', handler)
  }

  /**
   * Server-initiated request to begin tailing a service's log file. The
   * backend ships these after a frontend calls
   * `AgentHub.SubscribeToServiceLogs`, having already validated the
   * service name against the runtime's current spec. The composition root
   * wires this to `LogTailer.startTail` which spawns / ref-counts a
   * `tail -F` process. See `IRuntimeClient.startLogTail` on the .NET hub.
   */
  onStartLogTail(handler: (serviceName: string) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.startLogTail = handler
    this.#drainBuffer('StartLogTail', handler)
  }

  /**
   * Symmetric counterpart to `onStartLogTail`. The composition root wires
   * this to `LogTailer.stopTail` which decrements the ref-count and tears
   * the tail process down once it hits zero. See
   * `IRuntimeClient.stopLogTail` on the .NET hub.
   */
  onStopLogTail(handler: (serviceName: string) => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.stopLogTail = handler
    this.#drainBuffer('StopLogTail', handler)
  }

  /**
   * runtime-observability-super-admin — server-initiated request to begin
   * tailing the daemon's OWN stdout/stderr files
   * (`/var/log/supervisor/agent.out.log` + `agent.err.log`). Pushed by
   * `AgentHub.SubscribeToDaemonLogs` after the super-admin role gate passes.
   * The composition root wires this to a daemon-log LogTailer with
   * `initialLines: 200` so a fresh subscriber immediately sees recent
   * context. See `IRuntimeClient.startDaemonLogTail` on the .NET hub.
   */
  onStartDaemonLogTail(handler: () => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.startDaemonLogTail = handler
    // No-arg push: the buffered payloads are `undefined` (see the receiver
    // wrapper). Wrap the no-arg handler in the `(payload) => …` shape the
    // drain helper expects — payload is `undefined`, so the wrapper just
    // forwards a no-arg call.
    this.#drainBuffer<undefined>('StartDaemonLogTail', () => handler())
  }

  /**
   * Symmetric counterpart to `onStartDaemonLogTail`. Decrements the daemon-log
   * tailer's ref-count; on zero the underlying `tail -F` processes are
   * SIGTERM'd. See `IRuntimeClient.stopDaemonLogTail` on the .NET hub.
   */
  onStopDaemonLogTail(handler: () => void | Promise<void>): void {
    this.#requireConnection()
    this.#handlers.stopDaemonLogTail = handler
    this.#drainBuffer<undefined>('StopDaemonLogTail', () => handler())
  }

  /**
   * Phase-1 diff-view-tab handler. The server invokes
   * `IRuntimeClient.getChangedFiles` and awaits the daemon's reply (it's a
   * request/response, unlike the void slots above). The composition root
   * wires this to `GitModule.getChangedFiles`, which routes through the
   * existing serialisation queue so the read can't race an in-flight
   * commit / rebase / merge.
   */
  onGetChangedFiles(
    handler: (
      runtimeId: string,
      req: GeneratedChangedFilesRequest,
    ) => Promise<GeneratedChangedFilesResponse>,
  ): void {
    this.#requireConnection()
    this.#handlers.getChangedFiles = handler
  }

  /**
   * Phase-1 diff-view-tab handler. Symmetric counterpart to
   * `onGetChangedFiles`; routes to `GitModule.getFileDiff` which queues
   * the read alongside the other git ops. See
   * `IRuntimeClient.getFileDiff` on the .NET hub.
   */
  onGetFileDiff(
    handler: (
      runtimeId: string,
      req: GeneratedFileDiffRequest,
    ) => Promise<GeneratedFileDiffResponse>,
  ): void {
    this.#requireConnection()
    this.#handlers.getFileDiff = handler
  }

  /**
   * Phase-3 (compare-base) handler. The server invokes
   * `IRuntimeClient.getBranchChangedFiles` to list files differing between
   * two refs on the runtime — typically `main..HEAD` for the default
   * "compare against main" UX. The composition root wires this to
   * `GitModule.getBranchChangedFiles`, which queues the read alongside the
   * existing git ops so it can't race auto-commit / rebase work in flight.
   */
  onGetBranchChangedFiles(
    handler: (
      runtimeId: string,
      baseRef: string,
      headRef: string,
    ) => Promise<GeneratedChangedFilesResponse>,
  ): void {
    this.#requireConnection()
    this.#handlers.getBranchChangedFiles = handler
  }

  /**
   * Phase-3 (compare-base) handler. Symmetric counterpart to
   * `onGetBranchChangedFiles`; returns the unified diff text for a single
   * file between two refs. Routes through `GitModule.getBranchFileDiff`.
   */
  onGetBranchFileDiff(
    handler: (
      runtimeId: string,
      baseRef: string,
      headRef: string,
      path: string,
    ) => Promise<GeneratedFileDiffResponse>,
  ): void {
    this.#requireConnection()
    this.#handlers.getBranchFileDiff = handler
  }

  /**
   * Phase-3 (compare-base) handler. Returns the newest-first list of
   * commits in `base..head`, driving the commit-picker dropdown.
   * `limit` defaults to 200 on the daemon; hard-capped at 1000.
   */
  onGetCommitRange(
    handler: (
      runtimeId: string,
      baseRef: string,
      headRef: string,
      limit: number,
    ) => Promise<GeneratedCommitRangeResponse>,
  ): void {
    this.#requireConnection()
    this.#handlers.getCommitRange = handler
  }

  // ============================================================================
  // Lifecycle listeners — for orchestrator code that wants to know when the
  // wire is up/down (e.g. flushing buffered events on reconnect).
  // ============================================================================

  onConnected(cb: () => void): void {
    this.#connectedListeners.push(cb)
  }

  onDisconnected(cb: (error?: Error) => void): void {
    this.#disconnectedListeners.push(cb)
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  #requireConnection(): HubConnection {
    if (!this.#connection) {
      throw new Error('SignalRClient not started')
    }
    return this.#connection
  }

  #requireProxy(): IRuntimeHub {
    if (!this.#proxy) {
      throw new Error('SignalRClient not started')
    }
    return this.#proxy
  }

  /**
   * Wraps an inbound dispatch with three layers of safety:
   *
   *   1. Runtime-id guard — when the payload carries `runtimeId` and it does
   *      not match `config.runtimeId`, drop + warn. Today's .NET DTOs do not
   *      stamp this field; the guard is a no-op then. The hub's per-runtime
   *      group still enforces single-runtime routing server-side. Mismatched
   *      messages are discarded outright, NOT buffered — they were never
   *      meant for this runtime.
   *
   *   2. Early-arrival buffer — when no handler is registered yet, park the
   *      payload in `#buffers[method]` (bounded ring; drop-oldest on
   *      overflow). The matching `on*()` registrar drains the buffer when
   *      the handler is finally wired in. See `#buffers` for the full
   *      contract.
   *
   *   3. Handler-exception isolation — if the user's handler throws, log the
   *      error and keep the connection alive. A faulty handler must not take
   *      down the whole daemon's wire.
   */
  async #guardAndDispatch<T>(
    method: BufferableMethod,
    payload: T,
    handler: ((payload: T) => void | Promise<void>) | undefined,
  ): Promise<void> {
    if (payload !== null && typeof payload === 'object') {
      const stamped = (payload as { runtimeId?: unknown }).runtimeId
      if (typeof stamped === 'string' && stamped !== this.#config.runtimeId) {
        this.#logger.warn(
          {
            method,
            expected: this.#config.runtimeId,
            got: stamped,
          },
          'dropped inbound message: runtimeId mismatch',
        )
        return
      }
    }

    if (handler === undefined) {
      // Bootstrap-race buffer: park the payload so the eventual on*()
      // registrar can replay it. Drop-oldest on overflow keeps a misbehaving
      // server from OOMing the daemon. See `#buffers` for the full contract.
      const buf = this.#buffers.get(method) ?? []
      buf.push(payload)
      if (buf.length > MAX_BUFFERED_MESSAGES) {
        const dropped = buf.shift()
        this.#logger.warn(
          { method, dropped, bufferSize: MAX_BUFFERED_MESSAGES },
          'early-arrival buffer overflow: dropped oldest pending message',
        )
      }
      this.#buffers.set(method, buf)
      return
    }

    try {
      await handler(payload)
    } catch (err) {
      // Never let a handler throw kill the connection. The hub side will
      // not see this — failures are local to the daemon.
      this.#logger.error({ err, method }, 'inbound handler threw')
    }
  }

  /**
   * Drain any messages parked in `#buffers[method]` into the freshly-
   * registered handler. Called exactly once per `on*()` registrar (the
   * buffer is deleted after drain), so re-registering a handler with
   * `on*(handler2)` does NOT receive the old buffered messages — by that
   * point the buffer is empty.
   *
   * Sequencing: we replay in FIFO order (the same order the server sent),
   * sequentially awaiting each handler invocation so the second message
   * doesn't run until the first has settled. This matches the live
   * dispatch path in `#guardAndDispatch`, which also `await`s the handler.
   *
   * Fire-and-forget from the registrar: `on*()` returns synchronously
   * (callers expect a sync registration); the drain runs as a detached
   * promise. Any throw inside the handler is already caught + logged by
   * the per-message try/catch below, so the detached promise can never
   * reject.
   */
  #drainBuffer<T>(
    method: BufferableMethod,
    handler: (payload: T) => void | Promise<void>,
  ): void {
    const buf = this.#buffers.get(method)
    if (buf === undefined || buf.length === 0) {
      this.#buffers.delete(method)
      return
    }
    // Take + clear atomically before the async loop runs, so any further
    // inbound message that races the drain goes straight to the handler
    // (which is now set) rather than appending to a buffer we're already
    // consuming.
    this.#buffers.delete(method)
    const queue = buf as T[]
    void (async () => {
      for (const payload of queue) {
        try {
          await handler(payload)
        } catch (err) {
          this.#logger.error(
            { err, method, replayed: true },
            'inbound handler threw during buffered replay',
          )
        }
      }
    })()
  }
}
