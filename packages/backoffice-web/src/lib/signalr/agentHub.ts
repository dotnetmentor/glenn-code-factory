import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import {
  getHubProxyFactory,
  getReceiverRegister,
} from '@/generated/signalr/TypedSignalR.Client'
import type {
  IAgentClient,
  IAgentHub,
} from '@/generated/signalr/TypedSignalR.Client/Source.Features.SignalR.Hubs'
import type {
  AgentEventNotification,
  AttachmentStateChangedPayload,
  BootstrapProgressNotification,
  CancelTurnRequest,
  ConversationRenamedNotification,
  DaemonLogLineNotification,
  EventReplayRequest,
  LiveSupervisordSnapshotNotification,
  NotificationPayload,
  PermissionRequestedPayload,
  PreviewPortChangedNotification,
  ResolvePermissionPayload,
  RuntimeDiskPressureNotification,
  RuntimeEventNotification,
  RuntimeStateChangedNotification,
  RuntimeWakingNotification,
  ServiceLogLineNotification,
  SubmitPromptPayload,
  SubmitPromptResponse,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'
import type {
  RuntimeProposalCreatedPayload,
  RuntimeProposalUpdatedPayload,
} from '@/generated/signalr/Source.Features.RuntimeCuration.Models'
import type {
  CommitFailedPayload,
  CommitMadePayload,
  GitPushFailedPayload,
  GitPushSucceededPayload,
  MergeConflictPayload,
} from '@/generated/signalr/Source.Features.GitOps.Models'

/**
 * Listener-table key → payload type. Used internally to keep the per-event
 * handler sets typed without a manual switch on every call site.
 */
type AgentClientEventMap = {
  agentEvent: AgentEventNotification
  runtimeStateChanged: RuntimeStateChangedNotification
  bootstrapProgress: BootstrapProgressNotification
  runtimeWaking: RuntimeWakingNotification
  notification: NotificationPayload
  runtimeProposalCreated: RuntimeProposalCreatedPayload
  runtimeProposalUpdated: RuntimeProposalUpdatedPayload
  runtimeDiskPressure: RuntimeDiskPressureNotification
  conversationRenamed: ConversationRenamedNotification
  // Git-op fan-out for the "out-of-sync with GitHub" UX. The daemon emits these
  // via RuntimeHub; the backend relays to project groups on AgentHub. Wired
  // here so ChatCanvas can drive a banner / toast without polling.
  commitMade: CommitMadePayload
  commitFailed: CommitFailedPayload
  gitPushFailed: GitPushFailedPayload
  gitPushSucceeded: GitPushSucceededPayload
  mergeConflict: MergeConflictPayload
  // Inline-approval fan-out: the daemon's canUseTool callback fires through
  // RuntimeHub.PermissionRequested, the backend relays to project groups on
  // AgentHub.PermissionRequested, and the user's decision rides back via the
  // outbound resolvePermission helper below. Correlation by toolUseId.
  permissionRequested: PermissionRequestedPayload
  // Structured runtime events (Runtime Spec V2, Phase 4). Pushed from the
  // backend after a daemon's RuntimeEvent has been appended to the event
  // store. Drives the Timeline tab of the runtime drawer. Frontends opt in
  // per-runtime by calling subscribeToRuntimeEvents(runtimeId) on drawer
  // mount and unsubscribe on close.
  runtimeEventReceived: RuntimeEventNotification
  // On-demand SignalR log-tail lines (Runtime Spec V2, Phase 5). Pushed by
  // the backend whenever the daemon's `tail -F` produces a line; the
  // backend gates fan-out on the per-service group joined via
  // subscribeToServiceLogs. The Logs tab in the runtime drawer subscribes
  // on mount and unsubscribes on unmount — lines are NOT persisted, so a
  // subscriber that arrives late only sees lines from that moment forward.
  serviceLogLine: ServiceLogLineNotification
  // Super-admin runtime drawer Services tab. Pushed by the daemon's
  // ServiceStatusPoller every 10s tick (via daemon → backend → frontend
  // fan-out) to the runtime-events:{runtimeId} group — same group already
  // joined for runtimeEventReceived. Drives the live FATAL/BACKOFF/STOPPED
  // surface that the discrete-event Timeline can't represent end-to-end.
  liveSupervisordSnapshotReceived: LiveSupervisordSnapshotNotification
  // Super-admin runtime drawer Daemon Logs sub-tab. Pushed by the daemon's
  // own `tail -F` on /var/log/supervisor/agent.{out,err}.log. Group-gated:
  // backend only fans out to connections that called
  // subscribeToDaemonLogs(runtimeId).
  daemonLogLineReceived: DaemonLogLineNotification
  // Pushed when the project's preview port is hot-swapped via
  // PATCH /api/projects/{projectId}/preview-port. Fans out to every
  // branch-{branchId} group AND the parent workspace-{workspaceId} group
  // after the Cloudflare tunnels have been re-pointed at the new port — so
  // a receiver can trust the value is already live at the edge by the time
  // the push arrives. Drives the AppContainer iframe key-bump and refreshes
  // any open Preview settings field.
  previewPortChanged: PreviewPortChangedNotification
  // chat-file-attachments — per-chip terminal state push from the daemon
  // staging handshake. Backend fans this out on the branch-{branchId} group
  // after RuntimeHub.ReportAttachmentStaged resolves a staging attempt. The
  // chip composer subscribes via onAttachmentStateChanged and flips its
  // local "staging" UI to either Ready or the "Runtime download failed"
  // surface.
  attachmentStateChanged: AttachmentStateChangedPayload
}

type Listener<E extends keyof AgentClientEventMap> = (
  payload: AgentClientEventMap[E],
) => void

/**
 * State-change subscriber for React hooks that want to re-render when the
 * underlying SignalR connection moves between Connecting / Connected /
 * Reconnecting / Disconnected.
 */
export type StateListener = (state: HubConnectionState) => void

/**
 * Typed wrapper around a single AgentHub SignalR connection. Returned by
 * {@link createAgentHubConnection} and consumed by React hooks
 * (e.g. {@link useAgentHub}) and any non-React caller that needs the
 * generated typed proxy plus per-event listener fan-out.
 *
 * <p>The wrapper takes the place of the raw {@link HubConnection} for
 * application code: it surfaces the generated {@link IAgentHub} method shapes
 * for outbound invocation, fan-outs the {@link IAgentClient} receiver methods
 * to multiple listeners (the raw {@link HubConnection.on} only allows one
 * registration per method name without our own dispatch), and exposes the
 * connection state reactively.</p>
 */
export interface AgentHubConnection {
  /** Current SignalR connection state. Use {@link subscribeState} to react. */
  readonly state: HubConnectionState
  /** Idempotent — calling twice on a started connection is a no-op. */
  start(): Promise<void>
  /** Best-effort teardown; safe to call from a React effect cleanup. */
  stop(): Promise<void>
  /** Subscribe to connection-state transitions. Returns an unsubscribe. */
  subscribeState(listener: StateListener): () => void

  // ── inbound (server → client) ────────────────────────────────────────────
  onAgentEvent(handler: Listener<'agentEvent'>): () => void
  onRuntimeStateChanged(handler: Listener<'runtimeStateChanged'>): () => void
  onBootstrapProgress(handler: Listener<'bootstrapProgress'>): () => void
  onRuntimeWaking(handler: Listener<'runtimeWaking'>): () => void
  onNotification(handler: Listener<'notification'>): () => void
  onRuntimeProposalCreated(
    handler: Listener<'runtimeProposalCreated'>,
  ): () => void
  onRuntimeProposalUpdated(
    handler: Listener<'runtimeProposalUpdated'>,
  ): () => void
  onRuntimeDiskPressure(handler: Listener<'runtimeDiskPressure'>): () => void
  onConversationRenamed(handler: Listener<'conversationRenamed'>): () => void
  onCommitMade(handler: Listener<'commitMade'>): () => void
  onCommitFailed(handler: Listener<'commitFailed'>): () => void
  onGitPushFailed(handler: Listener<'gitPushFailed'>): () => void
  onGitPushSucceeded(handler: Listener<'gitPushSucceeded'>): () => void
  onMergeConflict(handler: Listener<'mergeConflict'>): () => void
  onPermissionRequested(handler: Listener<'permissionRequested'>): () => void
  onRuntimeEventReceived(
    handler: Listener<'runtimeEventReceived'>,
  ): () => void
  /**
   * Subscribe to live `tail -F` log lines for a single supervised service on
   * a single runtime. Only delivered to connections that have called
   * {@link subscribeToServiceLogs} for the matching (runtimeId, serviceName)
   * pair; the backend's hub method joins the caller to a
   * `service-logs:{runtimeId}:{serviceName}` group and bounces the request
   * down to the daemon so its LogTailer can spawn / refcount a tail.
   */
  onServiceLogLine(handler: Listener<'serviceLogLine'>): () => void
  /**
   * Subscribe to live supervisord XML-RPC snapshots for the runtime. Delivered
   * to the same `runtime-events:{runtimeId}` group as
   * {@link onRuntimeEventReceived}, so a single
   * {@link subscribeToRuntimeEvents} call wires both up. The snapshot is
   * not persisted — late subscribers see the next push (≤10s).
   */
  onLiveSupervisordSnapshotReceived(
    handler: Listener<'liveSupervisordSnapshotReceived'>,
  ): () => void
  /**
   * Subscribe to live daemon stdout/stderr tail lines for the runtime.
   * Super-admin only — the backend gates fan-out on the super-admin claim
   * and the `daemon-logs:{runtimeId}` group joined via
   * {@link subscribeToDaemonLogs}.
   */
  onDaemonLogLineReceived(
    handler: Listener<'daemonLogLineReceived'>,
  ): () => void
  /**
   * Subscribe to preview-port hot-swap events for the current project. The
   * Cloudflare edge has already been re-pointed by the time this fires, so
   * the typical handler reaction is to bump an iframe key (forcing a
   * reload against the new port) and invalidate the project query.
   */
  onPreviewPortChanged(handler: Listener<'previewPortChanged'>): () => void
  /**
   * Subscribe to per-attachment terminal-state pushes from the daemon
   * staging handshake. Fires once per attachment with `state: "Ready"` on
   * success or `state: "Failed"` (plus an `error` string) on daemon
   * failure. Scoped to the current branch by the backend's
   * `branch-{branchId}` group; sibling-branch tabs do not see each other.
   * Drives the per-chip "Staging on runtime…" → Ready / Failed transition.
   */
  onAttachmentStateChanged(
    handler: Listener<'attachmentStateChanged'>,
  ): () => void

  // ── outbound (client → server, generated proxy) ──────────────────────────
  submitPrompt(payload: SubmitPromptPayload): Promise<SubmitPromptResponse>
  cancelTurn(payload: CancelTurnRequest): Promise<void>
  requestEventReplay(
    payload: EventReplayRequest,
  ): Promise<AgentEventNotification[]>
  /**
   * React → server: ship the user's decision on an inline approval card back
   * to the daemon's pending canUseTool callback. Pure relay, correlated by
   * the payload's toolUseId — the hub resolves project → active runtime →
   * daemon connection and forwards via RuntimeClient.PermissionResolved.
   */
  resolvePermission(payload: ResolvePermissionPayload): Promise<void>
  /**
   * Join the runtime-events:{runtimeId} group so this connection receives
   * live runtimeEventReceived pushes for that runtime. Idempotent.
   */
  subscribeToRuntimeEvents(runtimeId: string): Promise<void>
  /** Symmetric counterpart — safe to call from a React effect cleanup. */
  unsubscribeFromRuntimeEvents(runtimeId: string): Promise<void>
  /**
   * Join the service-logs:{runtimeId}:{serviceName} group AND ask the
   * daemon to start (or ref-count) a `tail -F` on the matching log file.
   * Idempotent at both layers — multiple browser tabs share a single tail
   * process via the daemon's LogTailer reference counter.
   */
  subscribeToServiceLogs(
    runtimeId: string,
    serviceName: string,
  ): Promise<void>
  /**
   * Symmetric counterpart — safe to call from a React effect cleanup. The
   * daemon SIGTERMs the underlying `tail -F` when the last subscriber
   * detaches.
   */
  unsubscribeFromServiceLogs(
    runtimeId: string,
    serviceName: string,
  ): Promise<void>
  /**
   * Join the daemon-logs:{runtimeId} group and ask the backend to start (or
   * ref-count) the daemon's `tail -F` on its own stdout/stderr. Super-admin
   * gated server-side.
   */
  subscribeToDaemonLogs(runtimeId: string): Promise<void>
  /** Symmetric counterpart — safe to call from a React effect cleanup. */
  unsubscribeFromDaemonLogs(runtimeId: string): Promise<void>

  /**
   * Escape hatch for callers that need the raw connection (e.g. to register
   * handlers for events not yet covered by the wrapper, or to await
   * advanced lifecycle hooks). Prefer the typed `on…` helpers above.
   */
  readonly raw: HubConnection
}

/**
 * Options accepted by {@link createAgentHubConnection}.
 */
export interface CreateAgentHubConnectionOptions {
  /**
   * Project id passed as a query string. Used by the backend's
   * wake-on-connect path (a project owns one runtime per branch, but the
   * wake command is project-keyed). May be omitted — the AgentHub still
   * auto-joins the user group regardless.
   */
  projectId?: string
  /**
   * Branch id passed as a query string so the backend can join the
   * connection to the matching <c>branch-{id}</c> SignalR group on connect.
   * This is what scopes live AgentEvent broadcasts to the active branch tab
   * — without it, sibling-branch tabs would see each other's live
   * "Working…" indicators and assistant chunks after CopyBranch. Omit only
   * for connections that don't need live agent-event delivery (e.g. a
   * workspace-only sidebar mount).
   */
  branchId?: string
  /**
   * Override the base URL used to reach the hub. Defaults to a
   * same-origin relative path so cookie auth and Vite's `/api` proxy keep
   * working without extra config.
   */
  baseUrl?: string
}

/**
 * Build a typed AgentHub connection. The connection is constructed but NOT
 * started — call {@link AgentHubConnection.start} when you're ready (or let
 * a hook like {@link useAgentHub} do it).
 *
 * <p>Cookie auth is implicit: the React app already runs every fetch with
 * <c>withCredentials</c>, so SignalR's <c>withCredentials</c> default plus
 * <c>{ withCredentials: true }</c> on the builder is enough — no token
 * plumbing required.</p>
 */
/**
 * Module-level pool of live AgentHub connections, keyed by a deterministic
 * stringification of the {@link CreateAgentHubConnectionOptions} that built
 * them. Used by {@link acquireAgentHub} / {@link releaseAgentHub} so multiple
 * React hook instances on the same workspace branch reuse a single underlying
 * SignalR socket (and therefore a single {@code /hubs/agent/negotiate} call)
 * instead of opening N parallel negotiations on mount.
 *
 * <p>The {@link PoolEntry.stopTimer} field carries a deferred-teardown handle
 * so a fast unmount-then-remount cycle (StrictMode's intentional double mount
 * in dev; rapid branch swaps in prod) doesn't pay the cost of closing and
 * re-negotiating the socket — see {@link releaseAgentHub} for the grace
 * timer.</p>
 *
 * <p>Per-group ref counts ({@link PoolEntry.groupRefs}) collapse duplicate
 * SignalR group joins from independent consumers: two callers asking for the
 * same {@code runtime-events:{runtimeId}} fire only one server-side
 * {@code SubscribeToRuntimeEvents} invoke.</p>
 */
interface PoolEntry {
  conn: AgentHubConnection
  refCount: number
  stopTimer: ReturnType<typeof setTimeout> | null
  /** Per-group ref counts so duplicate subscribers collapse to one server call. */
  groupRefs: Map<string, number>
}

const connectionPool = new Map<string, PoolEntry>()

/**
 * Compute a deterministic pool key from connection options. Field order is
 * fixed so equivalent option objects always hash to the same string.
 */
function poolKey(opts: CreateAgentHubConnectionOptions): string {
  return JSON.stringify([
    opts.projectId ?? null,
    opts.branchId ?? null,
    opts.baseUrl ?? null,
  ])
}

/** Grace period before a zero-refcount connection is actually stopped (ms). */
const RELEASE_GRACE_MS = 250

/**
 * Per-group invoke key — pairs the group "kind" with its parameters so
 * (runtime-events, X) and (service-logs, X, Y) live in separate counters.
 */
function runtimeEventsGroupKey(runtimeId: string): string {
  return `runtime-events:${runtimeId}`
}
function serviceLogsGroupKey(runtimeId: string, serviceName: string): string {
  return `service-logs:${runtimeId}:${serviceName}`
}
function daemonLogsGroupKey(runtimeId: string): string {
  return `daemon-logs:${runtimeId}`
}

/**
 * Acquire a shared {@link AgentHubConnection} for the given options. If a
 * connection with the same key already exists in the pool, its refCount is
 * bumped and the existing wrapper is returned (cancelling any pending stop
 * timer). Otherwise a fresh connection is built, started, stored, and
 * returned.
 *
 * <p>Pair every call with exactly one {@link releaseAgentHub} call with the
 * same key. Multiple concurrent consumers on the same project/branch all
 * share the underlying socket — only the first acquire pays the negotiate
 * round-trip.</p>
 */
export function acquireAgentHub(
  opts: CreateAgentHubConnectionOptions = {},
): AgentHubConnection {
  const key = poolKey(opts)
  const existing = connectionPool.get(key)
  if (existing) {
    if (existing.stopTimer !== null) {
      clearTimeout(existing.stopTimer)
      existing.stopTimer = null
    }
    existing.refCount++
    return existing.conn
  }

  const groupRefs = new Map<string, number>()
  const baseConn = createAgentHubConnection(opts)
  // Wrap subscribe/unsubscribe so duplicate group joins from independent
  // consumers collapse to a single server-side invoke. The first subscriber
  // for a group pays the round-trip; subsequent subscribers piggy-back on
  // the existing join (still receiving fan-outs because the receiver tables
  // are shared). The last unsubscriber leaves the group.
  const pooledConn: AgentHubConnection = {
    ...baseConn,
    get state() {
      return baseConn.state
    },
    get raw() {
      return baseConn.raw
    },
    subscribeToRuntimeEvents(runtimeId: string) {
      const key = runtimeEventsGroupKey(runtimeId)
      const current = groupRefs.get(key) ?? 0
      groupRefs.set(key, current + 1)
      if (current === 0) {
        return baseConn.subscribeToRuntimeEvents(runtimeId)
      }
      return Promise.resolve()
    },
    unsubscribeFromRuntimeEvents(runtimeId: string) {
      const key = runtimeEventsGroupKey(runtimeId)
      const current = groupRefs.get(key) ?? 0
      if (current <= 1) {
        groupRefs.delete(key)
        return baseConn.unsubscribeFromRuntimeEvents(runtimeId)
      }
      groupRefs.set(key, current - 1)
      return Promise.resolve()
    },
    subscribeToServiceLogs(runtimeId: string, serviceName: string) {
      const key = serviceLogsGroupKey(runtimeId, serviceName)
      const current = groupRefs.get(key) ?? 0
      groupRefs.set(key, current + 1)
      if (current === 0) {
        return baseConn.subscribeToServiceLogs(runtimeId, serviceName)
      }
      return Promise.resolve()
    },
    unsubscribeFromServiceLogs(runtimeId: string, serviceName: string) {
      const key = serviceLogsGroupKey(runtimeId, serviceName)
      const current = groupRefs.get(key) ?? 0
      if (current <= 1) {
        groupRefs.delete(key)
        return baseConn.unsubscribeFromServiceLogs(runtimeId, serviceName)
      }
      groupRefs.set(key, current - 1)
      return Promise.resolve()
    },
    subscribeToDaemonLogs(runtimeId: string) {
      const key = daemonLogsGroupKey(runtimeId)
      const current = groupRefs.get(key) ?? 0
      groupRefs.set(key, current + 1)
      if (current === 0) {
        return baseConn.subscribeToDaemonLogs(runtimeId)
      }
      return Promise.resolve()
    },
    unsubscribeFromDaemonLogs(runtimeId: string) {
      const key = daemonLogsGroupKey(runtimeId)
      const current = groupRefs.get(key) ?? 0
      if (current <= 1) {
        groupRefs.delete(key)
        return baseConn.unsubscribeFromDaemonLogs(runtimeId)
      }
      groupRefs.set(key, current - 1)
      return Promise.resolve()
    },
  }

  const entry: PoolEntry = {
    conn: pooledConn,
    refCount: 1,
    stopTimer: null,
    groupRefs,
  }
  connectionPool.set(key, entry)

  // Eagerly start so the negotiate happens once. Failures are swallowed —
  // consumers can still subscribe to state and react to disconnected.
  pooledConn.start().catch((err: unknown) => {
    // eslint-disable-next-line no-console
    console.warn('[acquireAgentHub] start failed:', err)
  })

  return pooledConn
}

/**
 * Release a previously-acquired connection. Decrements the refcount; if it
 * reaches zero, schedules a deferred stop after {@link RELEASE_GRACE_MS} so
 * StrictMode's double-unmount + fast branch swaps don't pay the cost of
 * tearing down and rebuilding the socket.
 */
export function releaseAgentHub(
  opts: CreateAgentHubConnectionOptions = {},
): void {
  const key = poolKey(opts)
  const entry = connectionPool.get(key)
  if (!entry) return
  entry.refCount--
  if (entry.refCount > 0) return

  entry.stopTimer = setTimeout(() => {
    // Re-check refCount in case an acquire raced in during the grace window
    // (the acquire branch nulls stopTimer, so a non-null here means no race).
    const current = connectionPool.get(key)
    if (!current || current !== entry) return
    if (current.refCount > 0) {
      current.stopTimer = null
      return
    }
    connectionPool.delete(key)
    current.conn.stop().catch(() => {
      // best-effort
    })
  }, RELEASE_GRACE_MS)
}

export function createAgentHubConnection(
  opts: CreateAgentHubConnectionOptions = {},
): AgentHubConnection {
  const { projectId, branchId, baseUrl } = opts
  const base = baseUrl ?? ''
  const params = new URLSearchParams()
  if (projectId) params.set('projectId', projectId)
  if (branchId) params.set('branchId', branchId)
  const qs = params.toString()
  const url = qs ? `${base}/hubs/agent?${qs}` : `${base}/hubs/agent`

  const conn = new HubConnectionBuilder()
    .withUrl(url, { withCredentials: true })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  // ── per-event listener tables ───────────────────────────────────────────
  // We can't just expose connection.on() for each typed helper because the
  // generated receiver register binds every IAgentClient method name once.
  // Instead we register a single internal IAgentClient that fan-outs into
  // per-event Sets — multiple subscribers per event, individually typed.
  const listeners: {
    [E in keyof AgentClientEventMap]: Set<Listener<E>>
  } = {
    agentEvent: new Set(),
    runtimeStateChanged: new Set(),
    bootstrapProgress: new Set(),
    runtimeWaking: new Set(),
    notification: new Set(),
    runtimeProposalCreated: new Set(),
    runtimeProposalUpdated: new Set(),
    runtimeDiskPressure: new Set(),
    conversationRenamed: new Set(),
    commitMade: new Set(),
    commitFailed: new Set(),
    gitPushFailed: new Set(),
    gitPushSucceeded: new Set(),
    mergeConflict: new Set(),
    permissionRequested: new Set(),
    runtimeEventReceived: new Set(),
    serviceLogLine: new Set(),
    liveSupervisordSnapshotReceived: new Set(),
    daemonLogLineReceived: new Set(),
    previewPortChanged: new Set(),
    attachmentStateChanged: new Set(),
  }

  function fanOut<E extends keyof AgentClientEventMap>(
    event: E,
    payload: AgentClientEventMap[E],
  ): void {
    for (const listener of listeners[event]) {
      try {
        listener(payload)
      } catch (err) {
        // eslint-disable-next-line no-console
        console.error(`[AgentHub] listener for ${event} threw:`, err)
      }
    }
  }

  // The generated IAgentClient receiver covers *every* server-pushed method.
  // Methods we don't yet expose through the wrapper still need a receiver
  // method (otherwise SignalR logs "no client method named …" warnings on
  // every push) — so we provide async no-ops that satisfy the interface.
  const receiver: IAgentClient = {
    runtimeStateChanged: async (p) => fanOut('runtimeStateChanged', p),
    bootstrapProgress: async (p) => fanOut('bootstrapProgress', p),
    runtimeWaking: async (p) => fanOut('runtimeWaking', p),
    notification: async (p) => fanOut('notification', p),
    agentEvent: async (p) => fanOut('agentEvent', p),
    // Card 4 (cursor-native-chat-ux) — the daemon now ships a per-turn
    // RunResultDto alongside the terminal Status event. Wired as a no-op
    // here; cards 5–7 will surface this in the chat turn footer once the
    // visual layer lands. Receiver method must exist so SignalR doesn't
    // log "no client method named runResult" warnings on every push.
    runResult: async () => {},
    hookStarted: async () => {},
    hookProgress: async () => {},
    hookCompleted: async () => {},
    hookConfigError: async () => {},
    hookSelfHealStarted: async () => {},
    hookSelfHealMaxedOut: async () => {},
    gitOperationStarted: async () => {},
    gitOperationCompleted: async () => {},
    commitMade: async (p) => fanOut('commitMade', p),
    commitFailed: async (p) => fanOut('commitFailed', p),
    gitPushFailed: async (p) => fanOut('gitPushFailed', p),
    gitPushSucceeded: async (p) => fanOut('gitPushSucceeded', p),
    mergeConflict: async (p) => fanOut('mergeConflict', p),
    destructiveGitOpRequested: async () => {},
    turnRefused: async () => {},
    runtimeProposalCreated: async (p) => fanOut('runtimeProposalCreated', p),
    runtimeProposalUpdated: async (p) => fanOut('runtimeProposalUpdated', p),
    runtimeDiskPressure: async (p) => fanOut('runtimeDiskPressure', p),
    conversationRenamed: async (p) => fanOut('conversationRenamed', p),
    permissionRequested: async (p) => fanOut('permissionRequested', p),
    // Phase 4: fan out structured runtime events to drawer subscribers. The
    // backend only pushes these to connections that have explicitly opted
    // into the runtime-events:{runtimeId} group via subscribeToRuntimeEvents.
    runtimeEventReceived: async (p) => fanOut('runtimeEventReceived', p),
    // Phase 5: on-demand log-tail fan-out. Same group-gated push as runtime
    // events — the backend only sends here for connections subscribed via
    // subscribeToServiceLogs(runtimeId, serviceName).
    serviceLogLine: async (p) => fanOut('serviceLogLine', p),
    // Super-admin runtime drawer Services tab. Pushed by the daemon's
    // ServiceStatusPoller every 10s tick to the same
    // runtime-events:{runtimeId} group as runtimeEventReceived. The
    // useRuntimeEventStream hook fans this snapshot into its observable
    // state for ServicesTab / SysstatsPanel consumers.
    liveSupervisordSnapshotReceived: async (p) =>
      fanOut('liveSupervisordSnapshotReceived', p),
    // Super-admin runtime drawer Daemon Logs sub-tab. Same fan-out model
    // as serviceLogLine but for the daemon's own stdout/stderr.
    daemonLogLineReceived: async (p) => fanOut('daemonLogLineReceived', p),
    // Project-wide preview-port hot-swap. The connection auto-joins the
    // branch-{branchId} and workspace-{workspaceId} groups on connect (see
    // CreateAgentHubConnectionOptions), so any open workspace tab for the
    // project receives this without per-event subscribe.
    previewPortChanged: async (p) => fanOut('previewPortChanged', p),
    // chat-file-attachments — daemon staging-ack relay. Backend pushes one
    // call per attachment as RuntimeHub.ReportAttachmentStaged resolves
    // each row; the chip composer subscribes and flips its state.
    attachmentStateChanged: async (p) => fanOut('attachmentStateChanged', p),
  }
  const receiverDisposable = getReceiverRegister('IAgentClient').register(
    conn,
    receiver,
  )

  const proxy: IAgentHub = getHubProxyFactory('IAgentHub').createHubProxy(conn)

  // ── connection-state subscribers ────────────────────────────────────────
  const stateListeners = new Set<StateListener>()
  const emitState = () => {
    for (const l of stateListeners) {
      try {
        l(conn.state)
      } catch (err) {
        // eslint-disable-next-line no-console
        console.error('[AgentHub] state listener threw:', err)
      }
    }
  }
  conn.onreconnecting(emitState)
  conn.onreconnected(emitState)
  conn.onclose(emitState)

  function subscribe<E extends keyof AgentClientEventMap>(
    event: E,
    handler: Listener<E>,
  ): () => void {
    listeners[event].add(handler)
    return () => {
      listeners[event].delete(handler)
    }
  }

  let stopped = false

  return {
    get state() {
      return conn.state
    },
    raw: conn,
    async start() {
      if (
        conn.state === HubConnectionState.Connected ||
        conn.state === HubConnectionState.Connecting
      ) {
        return
      }
      try {
        await conn.start()
      } finally {
        emitState()
      }
    },
    async stop() {
      if (stopped) return
      stopped = true
      try {
        receiverDisposable.dispose()
      } catch {
        // best-effort
      }
      try {
        await conn.stop()
      } catch {
        // best-effort teardown — connection might already be closing
      } finally {
        emitState()
      }
    },
    subscribeState(listener) {
      stateListeners.add(listener)
      return () => {
        stateListeners.delete(listener)
      }
    },

    onAgentEvent: (h) => subscribe('agentEvent', h),
    onRuntimeStateChanged: (h) => subscribe('runtimeStateChanged', h),
    onBootstrapProgress: (h) => subscribe('bootstrapProgress', h),
    onRuntimeWaking: (h) => subscribe('runtimeWaking', h),
    onNotification: (h) => subscribe('notification', h),
    onRuntimeProposalCreated: (h) => subscribe('runtimeProposalCreated', h),
    onRuntimeProposalUpdated: (h) => subscribe('runtimeProposalUpdated', h),
    onRuntimeDiskPressure: (h) => subscribe('runtimeDiskPressure', h),
    onConversationRenamed: (h) => subscribe('conversationRenamed', h),
    onCommitMade: (h) => subscribe('commitMade', h),
    onCommitFailed: (h) => subscribe('commitFailed', h),
    onGitPushFailed: (h) => subscribe('gitPushFailed', h),
    onGitPushSucceeded: (h) => subscribe('gitPushSucceeded', h),
    onMergeConflict: (h) => subscribe('mergeConflict', h),
    onPermissionRequested: (h) => subscribe('permissionRequested', h),
    onRuntimeEventReceived: (h) => subscribe('runtimeEventReceived', h),
    onServiceLogLine: (h) => subscribe('serviceLogLine', h),
    onLiveSupervisordSnapshotReceived: (h) =>
      subscribe('liveSupervisordSnapshotReceived', h),
    onDaemonLogLineReceived: (h) => subscribe('daemonLogLineReceived', h),
    onPreviewPortChanged: (h) => subscribe('previewPortChanged', h),
    onAttachmentStateChanged: (h) => subscribe('attachmentStateChanged', h),

    submitPrompt: (payload) => proxy.submitPrompt(payload),
    cancelTurn: (payload) => proxy.cancelTurn(payload),
    requestEventReplay: (payload) => proxy.requestEventReplay(payload),
    resolvePermission: (payload) => proxy.resolvePermission(payload),
    subscribeToRuntimeEvents: (runtimeId) =>
      proxy.subscribeToRuntimeEvents(runtimeId),
    unsubscribeFromRuntimeEvents: (runtimeId) =>
      proxy.unsubscribeFromRuntimeEvents(runtimeId),
    subscribeToServiceLogs: (runtimeId, serviceName) =>
      proxy.subscribeToServiceLogs(runtimeId, serviceName),
    unsubscribeFromServiceLogs: (runtimeId, serviceName) =>
      proxy.unsubscribeFromServiceLogs(runtimeId, serviceName),
    subscribeToDaemonLogs: (runtimeId) =>
      proxy.subscribeToDaemonLogs(runtimeId),
    unsubscribeFromDaemonLogs: (runtimeId) =>
      proxy.unsubscribeFromDaemonLogs(runtimeId),
  }
}
