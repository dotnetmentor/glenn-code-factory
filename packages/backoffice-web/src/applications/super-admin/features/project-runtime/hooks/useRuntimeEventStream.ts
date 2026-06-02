import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  getApiRuntimeEvents,
  getGetApiRuntimeEventsQueryKey,
  type ListRuntimeEventsResponse,
  type RuntimeEventDto,
} from '@/api/queries-commands'
import type { AgentHubConnection } from '@/lib/signalr'
import { RuntimeState } from '@/api/queries-commands'
import type {
  LiveSupervisordSnapshotNotification,
  LiveSupervisordSnapshotPayload,
  RuntimeEventNotification,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'
import {
  isDaemonMidBootConnected,
  type RuntimeConnectivityStatus,
} from '@/applications/shared/runtime/runtimeDaemonConnectivity'

/**
 * Page size for both the initial REST fetch and each "load more" tap. The
 * backend hard-caps page size on the server side; this is the request hint.
 */
const PAGE_SIZE = 100

/**
 * Per-process row inside a parsed heartbeat sysstats snapshot. Mirrors the
 * daemon's {@code ProcessStats} contract in
 * {@code packages/daemon/src/sysstats/ProcessStatsCollector.ts}.
 */
export interface HeartbeatProcess {
  name: string
  pid: number
  rssBytes: number
  vmSizeBytes: number
  cpuPercent: number
}

/**
 * Network stats sample from the heartbeat snapshot. Null when the daemon
 * couldn't read any interface.
 */
export interface HeartbeatNetwork {
  interface: string
  rxBytes: number
  txBytes: number
  rxBytesPerSec: number
  txBytesPerSec: number
}

/**
 * Disk usage sample carried on the heartbeat. Mirrors
 * {@code DiskSamplePayload}.
 */
export interface HeartbeatDisk {
  usedBytes: number
  totalBytes: number
  sampledAt: string | Date
}

/**
 * Parsed heartbeat snapshot — disk usage + per-process sysstats + network
 * rates. Derived from the polled branch-scoped runtime/status response:
 * {@code lastDiskUsedBytes/TotalBytes/SampledAt} + {@code lastSysstatsSnapshot}
 * (JSON-encoded {@code SysstatsSnapshot} from the daemon). Heartbeat is not
 * pushed live over SignalR today — it lands on the status row and is exposed
 * here so SysstatsPanel + ServicesTab consume one shape.
 */
export interface HeartbeatSnapshot {
  /** Latest disk sample from the heartbeat, or null if not yet captured. */
  disk: HeartbeatDisk | null
  /** Top-50 supervised processes by RSS. Empty when no sample yet. */
  processes: HeartbeatProcess[]
  /** Network sample; null when daemon couldn't read an interface. */
  network: HeartbeatNetwork | null
  /** ISO timestamp the daemon sampled at. Null when no snapshot received. */
  sampledAt: string | null
}

/**
 * Return shape contract for {@link useRuntimeEventStream}.
 *
 * <p>This is the public contract consumed by ServicesTab, SysstatsPanel,
 * TimelineTab, and any other drawer surface that needs the live event /
 * supervisord / sysstats firehose. Keep field names + types stable.</p>
 *
 * <ul>
 *   <li>{@link events} — Reverse-chrono (newest first) merged feed of the
 *       initial REST page, paginated history, and live SignalR pushes.</li>
 *   <li>{@link supervisordSnapshot} — Latest XML-RPC poll from the daemon
 *       (≤10s old while connected). Null until the first push arrives.</li>
 *   <li>{@link heartbeatSnapshot} — Latest disk + sysstats + network from
 *       the heartbeat. Updates whenever a fresh runtime/status response
 *       lands. Null until the first heartbeat arrives.</li>
 *   <li>{@link isLive} — True when SignalR is connected and we are joined
 *       to the runtime-events group (i.e. new pushes will arrive).</li>
 *   <li>{@link hasMore} / {@link loadMore} — Infinite-scroll pagination for
 *       the event list.</li>
 * </ul>
 */
export interface UseRuntimeEventStreamReturn {
  events: RuntimeEventDto[]
  supervisordSnapshot: LiveSupervisordSnapshotPayload | null
  heartbeatSnapshot: HeartbeatSnapshot | null
  isLive: boolean
  hasMore: boolean
  loadingInitial: boolean
  loadingMore: boolean
  error: unknown
  loadMore: () => void
}

/**
 * Reactive runtime-event + live-snapshot stream for the super-admin drawer.
 *
 * <p>Four responsibilities, kept inside one hook so the page never has to
 * juggle four different loading states:
 * <ol>
 *   <li>Initial page fetch via the generated REST query
 *       {@code getApiRuntimeEvents} keyed on the runtimeId.</li>
 *   <li>Infinite-scroll older pages using the {@code before} cursor (the
 *       smallest timestamp in the current list).</li>
 *   <li>Live prepend of new events pushed over SignalR after the caller
 *       subscribes via the AgentHub {@code subscribeToRuntimeEvents}
 *       method — the backend joins the connection to
 *       {@code runtime-events:{runtimeId}} which fans out both
 *       {@code runtimeEventReceived} and {@code liveSupervisordSnapshotReceived}.</li>
 *   <li>Cache the latest supervisord snapshot from the same group so the
 *       Services tab / SysstatsPanel can read live FATAL/BACKOFF states the
 *       discrete-event Timeline can't represent end-to-end.</li>
 * </ol></p>
 *
 * <p>Every incoming SignalR event is also merged into the TanStack Query
 * cache for {@code useGetApiRuntimeEvents} keyed on {@code runtimeId}, so any
 * sibling consumer that reads the paged query (e.g. tests, future shared
 * components) sees the live event without re-fetching.</p>
 *
 * <p>The hook intentionally does <em>not</em> use {@code useInfiniteQuery} —
 * the live-prepend path needs to splice items into the head of the list
 * without re-keying every page boundary, which fights React Query's page
 * cache. A local {@code events} state owned by the hook is simpler and the
 * extra cost is one re-render per push.</p>
 */
export function useRuntimeEventStream(params: {
  connection: AgentHubConnection | null
  runtimeId: string | undefined
  /**
   * Latest poll-fetched status — used to derive the heartbeat snapshot
   * (disk + sysstats + network) for SysstatsPanel consumers. Optional;
   * if omitted the hook still works but {@code heartbeatSnapshot} stays
   * null.
   */
  status?: RuntimeConnectivityStatus | null
  /** Skip both REST + SignalR while false. Used to inert the hook while the drawer is closed. */
  enabled: boolean
}): UseRuntimeEventStreamReturn {
  const { connection, runtimeId, status, enabled } = params
  const queryClient = useQueryClient()

  const [events, setEvents] = useState<RuntimeEventDto[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [loadingInitial, setLoadingInitial] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [supervisordSnapshot, setSupervisordSnapshot] =
    useState<LiveSupervisordSnapshotPayload | null>(null)
  const [isLive, setIsLive] = useState(false)

  // Tracks the currently-loaded runtime so a runtimeId change resets state
  // without a stale-page flicker. Also gates the SignalR subscription effect.
  const activeRuntimeRef = useRef<string | undefined>(undefined)

  // ── Initial fetch ────────────────────────────────────────────────────────
  useEffect(() => {
    if (!enabled || !runtimeId) {
      setEvents([])
      setHasMore(false)
      setLoadingInitial(false)
      setLoadingMore(false)
      setError(null)
      setSupervisordSnapshot(null)
      setIsLive(false)
      activeRuntimeRef.current = undefined
      return
    }

    let cancelled = false
    activeRuntimeRef.current = runtimeId
    setLoadingInitial(true)
    setError(null)

    getApiRuntimeEvents({ runtimeId, limit: PAGE_SIZE })
      .then((res) => {
        if (cancelled || activeRuntimeRef.current !== runtimeId) return
        setEvents(res.events ?? [])
        setHasMore(Boolean(res.hasMore))
        setLoadingInitial(false)
      })
      .catch((err: unknown) => {
        if (cancelled || activeRuntimeRef.current !== runtimeId) return
        setError(err)
        setLoadingInitial(false)
      })

    return () => {
      cancelled = true
    }
  }, [enabled, runtimeId])

  // ── Pagination ───────────────────────────────────────────────────────────
  const loadMore = useCallback(() => {
    if (!enabled || !runtimeId) return
    if (!hasMore || loadingMore || loadingInitial) return
    const oldest = events[events.length - 1]
    if (!oldest) return

    setLoadingMore(true)
    getApiRuntimeEvents({
      runtimeId,
      limit: PAGE_SIZE,
      before: typeof oldest.timestamp === 'string'
        ? oldest.timestamp
        : new Date(oldest.timestamp).toISOString(),
    })
      .then((res) => {
        if (activeRuntimeRef.current !== runtimeId) return
        setEvents((prev) => {
          // Dedupe by id in case the server's "before" boundary is inclusive
          // on some implementations — defensive, cheap.
          const known = new Set(prev.map((e) => e.id))
          const additions = (res.events ?? []).filter((e) => !known.has(e.id))
          return [...prev, ...additions]
        })
        setHasMore(Boolean(res.hasMore))
        setLoadingMore(false)
      })
      .catch((err: unknown) => {
        if (activeRuntimeRef.current !== runtimeId) return
        setError(err)
        setLoadingMore(false)
      })
  }, [enabled, runtimeId, hasMore, loadingMore, loadingInitial, events])

  // ── Live updates via SignalR ─────────────────────────────────────────────
  useEffect(() => {
    if (!enabled || !connection || !runtimeId) {
      setIsLive(false)
      return
    }

    let subscribed = false
    // Best-effort subscribe; if the connection isn't yet in Connected state
    // the invoke will throw and the catch below handles it. SignalR's
    // built-in withAutomaticReconnect re-runs OnConnectedAsync server-side on
    // every reconnect, but our group memberships are per-connection and
    // don't survive the disconnect — we re-subscribe explicitly here when
    // the runtimeId / connection changes; full reconnect-aware
    // re-subscription is a larger refactor tracked separately. For V2 the
    // REST initial fetch on drawer re-open covers any pushes missed during
    // a reconnect blip.
    connection
      .subscribeToRuntimeEvents(runtimeId)
      .then(() => {
        subscribed = true
        setIsLive(true)
      })
      .catch((err: unknown) => {
        // eslint-disable-next-line no-console
        console.warn('[useRuntimeEventStream] subscribe failed:', err)
        setIsLive(false)
      })

    // Track connection state — flip isLive off if the underlying connection
    // drops, on again when it reconnects (SignalR auto-reconnect runs
    // OnConnectedAsync on the server but the group membership is gone so
    // pushes stop until we re-subscribe — best-effort do that here too).
    const unsubState = connection.subscribeState((state) => {
      const connected = String(state) === 'Connected'
      setIsLive(connected && subscribed)
      if (connected && runtimeId) {
        // Re-attempt the group join on reconnect. Idempotent server-side.
        connection
          .subscribeToRuntimeEvents(runtimeId)
          .then(() => {
            subscribed = true
            setIsLive(true)
          })
          .catch(() => {
            // best-effort
          })
      }
    })

    const unsubEvent = connection.onRuntimeEventReceived(
      (payload: RuntimeEventNotification) => {
        if (payload.runtimeId !== runtimeId) return
        // Map the SignalR payload (Date | string for timestamp, payload as
        // JSON string) onto the REST DTO shape so the rest of the UI is
        // oblivious to the source. {@code payload} from SignalR is a JSON
        // string per the backend contract; the DTO declares it as unknown.
        const ts =
          typeof payload.timestamp === 'string'
            ? payload.timestamp
            : payload.timestamp.toISOString()
        const dto: RuntimeEventDto = {
          id: payload.id,
          runtimeId: payload.runtimeId,
          type: payload.type,
          severity: payload.severity,
          timestamp: ts,
          durationMs: payload.durationMs ?? null,
          payload: payload.payload,
        }
        setEvents((prev) => {
          // Don't double-add — the REST initial fetch could race the first
          // SignalR push if both arrive in the same tick.
          if (prev.some((e) => e.id === dto.id)) return prev
          return [dto, ...prev]
        })

        // Mirror into TanStack Query so any sibling consumer of
        // useGetApiRuntimeEvents for this runtime stays in sync. We touch
        // the queries-without-cursor cache only — paginated `before=`
        // queries are immutable history windows and shouldn't be patched.
        queryClient.setQueryData<ListRuntimeEventsResponse>(
          getGetApiRuntimeEventsQueryKey({ runtimeId, limit: PAGE_SIZE }),
          (prev) => {
            if (!prev) return prev
            if ((prev.events ?? []).some((e) => e.id === dto.id)) return prev
            return {
              ...prev,
              events: [dto, ...(prev.events ?? [])],
            }
          },
        )
      },
    )

    const unsubSnapshot = connection.onLiveSupervisordSnapshotReceived(
      (payload: LiveSupervisordSnapshotNotification) => {
        if (payload.runtimeId !== runtimeId) return
        // The backend wraps the daemon-side LiveSupervisordSnapshotPayload
        // with a runtimeId for fan-out. Strip that field — consumers just
        // want the snapshot.
        setSupervisordSnapshot({
          sampledAt: payload.sampledAt,
          processes: payload.processes,
        })
      },
    )

    return () => {
      unsubState()
      unsubEvent()
      unsubSnapshot()
      setIsLive(false)
      if (subscribed) {
        connection.unsubscribeFromRuntimeEvents(runtimeId).catch(() => {
          // Best-effort teardown — connection may already be closing.
        })
      }
    }
  }, [enabled, connection, runtimeId, queryClient])

  // Drop cached supervisord rows when live pushes stop — otherwise the Services
  // tab keeps showing the last RUNNING snapshot from before a crash / force-stop
  // even though no daemon is connected to refresh it.
  useEffect(() => {
    if (!enabled) return

    const state = status?.state as RuntimeState | undefined
    const terminal =
      state === RuntimeState.Failed ||
      state === RuntimeState.Crashed ||
      state === RuntimeState.Pending ||
      state === RuntimeState.Suspended ||
      state === RuntimeState.Suspending ||
      state === RuntimeState.Deleting ||
      state === RuntimeState.Deleted

    if (terminal) {
      setSupervisordSnapshot(null)
      return
    }

    if (!isLive && status && !isDaemonMidBootConnected(status)) {
      setSupervisordSnapshot(null)
    }
  }, [enabled, isLive, status])

  // ── Derived heartbeat snapshot from polled status row ───────────────────
  const heartbeatSnapshot = useMemo<HeartbeatSnapshot | null>(() => {
    if (!status) return null
    const hasDisk =
      status.lastDiskUsedBytes != null &&
      status.lastDiskTotalBytes != null &&
      status.lastDiskSampledAt != null
    const disk: HeartbeatDisk | null = hasDisk
      ? {
          usedBytes: status.lastDiskUsedBytes as number,
          totalBytes: status.lastDiskTotalBytes as number,
          sampledAt: status.lastDiskSampledAt as string,
        }
      : null

    let processes: HeartbeatProcess[] = []
    let network: HeartbeatNetwork | null = null
    let sampledAt: string | null = null
    if (status.lastSysstatsSnapshot) {
      try {
        const parsed = JSON.parse(status.lastSysstatsSnapshot) as {
          sampledAt?: string
          processes?: HeartbeatProcess[]
          network?: HeartbeatNetwork | null
        }
        sampledAt = parsed.sampledAt ?? null
        processes = Array.isArray(parsed.processes) ? parsed.processes : []
        network = parsed.network ?? null
      } catch {
        // Malformed snapshot — surface as "no sysstats". Disk still shows
        // if the disk fields are present.
      }
    }

    if (!disk && processes.length === 0 && !network) return null
    return { disk, processes, network, sampledAt }
  }, [status])

  return useMemo<UseRuntimeEventStreamReturn>(
    () => ({
      events,
      supervisordSnapshot,
      heartbeatSnapshot,
      isLive,
      hasMore,
      loadingInitial,
      loadingMore,
      error,
      loadMore,
    }),
    [
      events,
      supervisordSnapshot,
      heartbeatSnapshot,
      isLive,
      hasMore,
      loadingInitial,
      loadingMore,
      error,
      loadMore,
    ],
  )
}
