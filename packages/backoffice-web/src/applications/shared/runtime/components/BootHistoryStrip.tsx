import { useMemo } from 'react'
import { Box, Stack, Tooltip } from '@mui/material'
import type { RuntimeEventDto } from '@/api/queries-commands'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'

/**
 * Tiny "last 5 boots" sparkline that sits next to the heartbeat caption in the
 * RuntimeStatusHeader. Each square is a single boot attempt — green for
 * success, red for failure, amber for an attempt still in flight — newest on
 * the right. Hover any square for an absolute timestamp + outcome + total
 * duration.
 *
 * <p><b>Data source choice.</b> The backend has a {@code BootstrapRun} entity
 * and a super-admin-only {@code GET /api/admin/bootstrap-runs} endpoint, but
 * (a) those rows are only written by the GetBootstrap query handler (fetch
 * stage = "Fetching", never Ready) — they do not yet represent a full boot
 * attempt — and (b) the endpoint is gated to SuperAdmin so it would 403 for
 * the regular workspace debug panel. Both surfaces (super-admin
 * RuntimeDrawer + everyone's RuntimeLogsPanel) already have a live runtime
 * event stream wired in, so we derive boot history from those events
 * instead. When the daemon-architecture spec lands and {@code BootstrapRun}
 * rows become the canonical source, this component can swap to the
 * {@code useGetApiAdminBootstrapRuns} hook with no caller changes (just an
 * additional prop variant).</p>
 *
 * <p><b>Derivation rules.</b> A boot is a cluster of {@code BootstrapStage*}
 * events:
 * <ul>
 *   <li>Cluster by {@code payload.bootAttemptNumber} when present;
 *       otherwise cluster by adjacency (events within 5 minutes of each
 *       other belong to the same boot).</li>
 *   <li>Outcome = <b>failed</b> if any event in the cluster is
 *       {@code BootstrapStageFailed}; otherwise <b>succeeded</b> if a
 *       {@code BootstrapStageCompleted} with {@code stage === 'finalize'}
 *       / {@code stage === 'all'} or with a {@code bootstrapTotalMs} payload
 *       is present; otherwise <b>booting</b> (in-flight).</li>
 *   <li>Take the last 5 distinct boots, newest on the right.</li>
 * </ul></p>
 */

const BOOTSTRAP_EVENT_TYPES = new Set([
  'BootstrapStageStarted',
  'BootstrapStageCompleted',
  'BootstrapStageFailed',
])

/** Boots without a {@code bootAttemptNumber} payload get clustered by adjacency. */
const ADJACENCY_WINDOW_MS = 5 * 60 * 1000

type BootOutcome = 'succeeded' | 'failed' | 'booting'

interface BootCluster {
  /** Earliest timestamp in the cluster — when this boot began. */
  startedAt: Date
  /** Latest timestamp in the cluster — used for the tooltip header. */
  lastEventAt: Date
  outcome: BootOutcome
  /** Sum of {@code durationMs} on every event in the cluster, when present. */
  totalMs: number | null
}

export interface BootHistoryStripProps {
  /** Reverse-chrono runtime event feed from {@code useRuntimeEventStream}. */
  events: RuntimeEventDto[] | undefined
}

export function BootHistoryStrip({ events }: BootHistoryStripProps) {
  const boots = useMemo(() => deriveBoots(events), [events])

  if (boots.length === 0) {
    // Greenfield runtime — hide the strip entirely rather than rendering an
    // empty container that would steal layout space next to the heartbeat.
    return null
  }

  return (
    <Stack
      direction="row"
      spacing={0.25}
      alignItems="center"
      aria-label="Recent boot history"
      sx={{
        // Hide on narrow viewports — the row already runs hot with chip +
        // phase + respawn + heartbeat; sparkline is the first thing to drop.
        display: { xs: 'none', sm: 'flex' },
        gap: '2px',
      }}
    >
      {boots.map((boot, idx) => (
        <Tooltip
          key={`${boot.startedAt.getTime()}-${idx}`}
          title={`Boot at ${boot.startedAt.toLocaleString()}: ${boot.outcome} · ${
            boot.totalMs != null ? formatTotalMs(boot.totalMs) : '—'
          }`}
        >
          <Box
            aria-hidden
            sx={{
              width: 10,
              height: 10,
              borderRadius: 1,
              flexShrink: 0,
              backgroundColor: outcomeColor(boot.outcome),
              // Hairline border so back-to-back same-coloured squares stay
              // visually distinct against the chrome background.
              border: '1px solid rgba(0, 0, 0, 0.08)',
            }}
          />
        </Tooltip>
      ))}
    </Stack>
  )
}

function outcomeColor(outcome: BootOutcome): string {
  switch (outcome) {
    case 'succeeded':
      return workspaceRuntime.online
    case 'failed':
      return workspaceRuntime.failed
    case 'booting':
      return workspaceRuntime.booting
  }
}

function formatTotalMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`
  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(1)}s`
  const minutes = Math.floor(seconds / 60)
  const remainder = Math.round(seconds - minutes * 60)
  return `${minutes}m ${remainder}s`
}

/**
 * Walk the (reverse-chrono) event feed and bucket bootstrap-stage events into
 * boot clusters. See class doc for the rules — exported only so tests can
 * exercise the cluster logic directly.
 */
function deriveBoots(events: RuntimeEventDto[] | undefined): BootCluster[] {
  if (!events || events.length === 0) return []

  // Walk forwards in time so adjacency clustering reads naturally. The
  // incoming feed is newest-first, so reverse a shallow copy.
  const bootstrapEvents = events
    .filter((ev) => BOOTSTRAP_EVENT_TYPES.has(ev.type))
    .slice()
    .reverse()
  if (bootstrapEvents.length === 0) return []

  type WorkingCluster = {
    bootKey: string | null
    startedAt: Date
    lastEventAt: Date
    succeededHit: boolean
    failedHit: boolean
    totalMs: number | null
    bootstrapTotalMs: number | null
  }

  const clusters: WorkingCluster[] = []

  for (const ev of bootstrapEvents) {
    const ts = parseTimestamp(ev.timestamp)
    if (!ts) continue

    const payload = readPayload(ev.payload)
    const bootKey = readBootAttemptKey(payload)
    const stage = readStage(payload)
    const bootstrapTotalMs = readBootstrapTotalMs(payload)

    // Find or open a cluster. Explicit bootAttemptNumber wins; otherwise we
    // fall back to "extend the most recent cluster if it's within the
    // 5-minute adjacency window".
    let cluster: WorkingCluster | undefined
    if (bootKey) {
      cluster = clusters.find((c) => c.bootKey === bootKey)
    }
    if (!cluster) {
      const tail = clusters[clusters.length - 1]
      if (
        tail &&
        !tail.bootKey &&
        !bootKey &&
        ts.getTime() - tail.lastEventAt.getTime() <= ADJACENCY_WINDOW_MS
      ) {
        cluster = tail
      }
    }
    if (!cluster) {
      cluster = {
        bootKey,
        startedAt: ts,
        lastEventAt: ts,
        succeededHit: false,
        failedHit: false,
        totalMs: null,
        bootstrapTotalMs: null,
      }
      clusters.push(cluster)
    }

    if (ts.getTime() < cluster.startedAt.getTime()) cluster.startedAt = ts
    if (ts.getTime() > cluster.lastEventAt.getTime()) cluster.lastEventAt = ts

    if (ev.type === 'BootstrapStageFailed') {
      cluster.failedHit = true
    } else if (ev.type === 'BootstrapStageCompleted') {
      // Success-final markers: explicit "finalize" / "all" stage, or any
      // event carrying a bootstrapTotalMs (only emitted by the daemon's
      // "boot complete" event).
      if (stage === 'finalize' || stage === 'all' || bootstrapTotalMs != null) {
        cluster.succeededHit = true
      }
    }

    if (bootstrapTotalMs != null) {
      cluster.bootstrapTotalMs = bootstrapTotalMs
    }
    if (typeof ev.durationMs === 'number' && Number.isFinite(ev.durationMs)) {
      cluster.totalMs = (cluster.totalMs ?? 0) + ev.durationMs
    }
  }

  // Reduce to render-ready clusters. Outcome precedence: failed > succeeded >
  // booting — a cluster that hit both a failure and a success is treated as
  // failed (the failure is the louder signal for the operator).
  const reduced: BootCluster[] = clusters.map((c) => ({
    startedAt: c.startedAt,
    lastEventAt: c.lastEventAt,
    outcome: c.failedHit
      ? 'failed'
      : c.succeededHit
        ? 'succeeded'
        : 'booting',
    totalMs: c.bootstrapTotalMs ?? c.totalMs,
  }))

  // Keep the last 5 boots, newest on the right (the natural order after
  // sorting ascending and slicing the tail).
  reduced.sort((a, b) => a.startedAt.getTime() - b.startedAt.getTime())
  return reduced.slice(-5)
}

function parseTimestamp(value: unknown): Date | null {
  if (value instanceof Date) {
    return Number.isNaN(value.getTime()) ? null : value
  }
  if (typeof value === 'string') {
    const d = new Date(value)
    return Number.isNaN(d.getTime()) ? null : d
  }
  return null
}

function readPayload(raw: unknown): Record<string, unknown> | null {
  if (!raw) return null
  if (typeof raw === 'object' && !Array.isArray(raw)) {
    return raw as Record<string, unknown>
  }
  if (typeof raw === 'string') {
    try {
      const parsed = JSON.parse(raw)
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return parsed as Record<string, unknown>
      }
    } catch {
      // Daemon occasionally emits a non-JSON message string in payload;
      // that's fine — no cluster keys to read.
    }
  }
  return null
}

function readBootAttemptKey(payload: Record<string, unknown> | null): string | null {
  if (!payload) return null
  const raw = payload.bootAttemptNumber
  if (typeof raw === 'number' && Number.isFinite(raw)) return String(raw)
  if (typeof raw === 'string' && raw.length > 0) return raw
  return null
}

function readStage(payload: Record<string, unknown> | null): string | null {
  if (!payload) return null
  const raw = payload.stage
  return typeof raw === 'string' ? raw.toLowerCase() : null
}

function readBootstrapTotalMs(payload: Record<string, unknown> | null): number | null {
  if (!payload) return null
  const raw = payload.bootstrapTotalMs
  if (typeof raw === 'number' && Number.isFinite(raw)) return raw
  return null
}
