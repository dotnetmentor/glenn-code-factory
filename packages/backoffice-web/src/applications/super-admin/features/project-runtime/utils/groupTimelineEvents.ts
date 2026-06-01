/**
 * Group repeated identical events in the Timeline tab.
 *
 * <p>When a long burst of structurally-identical events lands in the stream
 * (e.g. 791× {@code ServiceStarting} during a runtime boot) the Timeline
 * becomes unreadable. The pure {@link groupTimelineEvents} function below
 * collapses N ≥ 3 consecutive events that share {@code type} and the
 * {@code serviceName} / {@code stage} payload fields AND fall inside a
 * 60-second window into a single {@link TimelineGroup} of kind
 * {@code 'group'}. Singleton / non-matching runs pass through as
 * {@code 'single'} entries so the list keeps its original order.</p>
 *
 * <p>Kept as a pure helper (no React, no MUI) so it's straightforward to unit
 * test and reuse. The Timeline component receives the events array
 * already-sorted in reverse-chronological order and we preserve that
 * ordering on output.</p>
 */

import type { RuntimeEventDto } from '@/api/queries-commands'
import { parseEventPayload } from './runtimeEventDisplay'

/** Maximum span (in ms) a group can cover, edge-to-edge. */
export const GROUP_WINDOW_MS = 60_000

/** Minimum run length to collapse. Two-in-a-row stays expanded for legibility. */
export const GROUP_MIN_COUNT = 3

/**
 * A single Timeline entry — either a one-off event or a collapsed group of
 * N ≥ 3 identical events.
 */
export type TimelineGroup =
  | { kind: 'single'; event: RuntimeEventDto }
  | {
      kind: 'group'
      type: string
      count: number
      /** Earliest occurrence in the group (oldest timestamp). */
      earliest: RuntimeEventDto
      /** Latest occurrence in the group (newest timestamp). */
      latest: RuntimeEventDto
      events: RuntimeEventDto[]
    }

/**
 * Pull a stable "shape" string out of an event so we know whether the next
 * event extends the current run. We key on the event {@code type} plus the
 * {@code serviceName} and {@code stage} payload fields when present — those
 * are the two fields that vary across the heavy-emit boot events
 * (ServiceStarting, BootstrapStageStarted, etc.).
 */
function groupKey(event: RuntimeEventDto): string {
  const type = event.type ?? ''
  const payload = parseEventPayload(event.payload)
  const serviceName =
    payload && typeof payload['serviceName'] === 'string'
      ? payload['serviceName']
      : ''
  const stage =
    payload && typeof payload['stage'] === 'string' ? payload['stage'] : ''
  return `${type}::${serviceName}::${stage}`
}

/**
 * Parse the event's timestamp. Tolerates the two field names we see in the
 * wild — {@code timestamp} (REST DTO) and {@code occurredAt} (legacy SignalR
 * frame). Falls back to {@code NaN} for events with no usable timestamp,
 * which then never join a window-bounded group.
 */
function eventTimeMs(event: RuntimeEventDto): number {
  const ts = event.timestamp ?? (event as { occurredAt?: string }).occurredAt
  if (!ts) return Number.NaN
  const ms = new Date(ts).getTime()
  return Number.isFinite(ms) ? ms : Number.NaN
}

/**
 * Walk the input list and collapse runs of identical events. Events arrive
 * reverse-chronological (newest first). Within a run, the first item is
 * therefore the latest and the last item is the earliest. The 60-second
 * window is computed edge-to-edge from those two.
 *
 * <p>Pure / deterministic / no side effects — safe to memoise on the input
 * array reference at the call site.</p>
 */
export function groupTimelineEvents(events: RuntimeEventDto[]): TimelineGroup[] {
  const out: TimelineGroup[] = []
  let i = 0
  while (i < events.length) {
    const head = events[i]
    if (!head) {
      i += 1
      continue
    }
    const key = groupKey(head)
    const headMs = eventTimeMs(head)

    // Greedy extend: walk forward while the key matches AND the window
    // (from the current run's latest, which is `head`) stays within 60s.
    let j = i + 1
    while (j < events.length) {
      const next = events[j]
      if (!next) break
      if (groupKey(next) !== key) break
      if (Number.isFinite(headMs)) {
        const nextMs = eventTimeMs(next)
        if (!Number.isFinite(nextMs)) break
        // events are reverse-chronological: head is newest, next is older.
        if (headMs - nextMs > GROUP_WINDOW_MS) break
      }
      j += 1
    }

    const runLen = j - i
    if (runLen >= GROUP_MIN_COUNT) {
      const run = events.slice(i, j)
      // Reverse-chrono: run[0] is latest, run[run.length-1] is earliest.
      const latest = run[0]!
      const earliest = run[run.length - 1]!
      out.push({
        kind: 'group',
        type: head.type ?? '',
        count: runLen,
        earliest,
        latest,
        events: run,
      })
    } else {
      // Short run — emit each event as a singleton so visual rhythm is kept.
      for (let k = i; k < j; k += 1) {
        const ev = events[k]
        if (ev) out.push({ kind: 'single', event: ev })
      }
    }
    i = j
  }
  return out
}

/**
 * Test-friendly export bundle. Lets specs import a single object instead of
 * importing every helper individually, mirroring the convention used by
 * {@code runtimeEventDisplay}.
 */
export const __test__ = {
  groupKey,
  eventTimeMs,
  GROUP_WINDOW_MS,
  GROUP_MIN_COUNT,
}
