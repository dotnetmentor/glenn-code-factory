/**
 * Display helpers for {@code RuntimeEventDto}-shaped records. Keeps the
 * formatting concerns out of the React components — pure functions, easy to
 * unit-test, no MUI / icon dependencies.
 *
 * <p>The structured event taxonomy is fixed in the daemon
 * (see RuntimeEventTypes in Source.Features.Runtime). The strings here mirror
 * those names rather than depending on a shared enum, because the REST DTO
 * carries the type as a plain string and we want to render unknown types
 * gracefully (a daemon emitting a brand-new event should not crash the UI).</p>
 */

import type { RuntimeEventDto } from '@/api/queries-commands'

/**
 * Event-type prefixes used for the Timeline filter chips. The mapping from
 * chip → predicate is data-driven so adding a new chip is just a new entry.
 *
 * <p>Buckets:
 * <ul>
 *   <li>{@code boot} — Bootstrap*, Install*, Setup* (the boot-time work).</li>
 *   <li>{@code services} — Service* lifecycle (Started/Crashed/Restarted/
 *       FailedToStart).</li>
 *   <li>{@code health} — Heartbeat* + DiskPressure* + RuntimeRespawnTriggered
 *       + CloudflaredTunnel* (the runtime-level health signals).</li>
 *   <li>{@code errors} — any event with severity=Error, regardless of type.</li>
 * </ul></p>
 */
export type TimelineFilter =
  | 'all'
  | 'boot'
  | 'services'
  | 'health'
  | 'errors'

export const TIMELINE_FILTERS: ReadonlyArray<{
  value: TimelineFilter
  label: string
}> = [
  { value: 'all', label: 'All' },
  { value: 'boot', label: 'Boot' },
  { value: 'services', label: 'Services' },
  { value: 'health', label: 'Health' },
  { value: 'errors', label: 'Errors' },
]

/**
 * Whether an event matches the active filter chip. Errors are severity-based;
 * the rest filter on the event-type prefix. Case-insensitive to be robust
 * against minor naming drift between daemon emit and DTO serialization.
 */
export function matchesFilter(
  event: RuntimeEventDto,
  filter: TimelineFilter,
): boolean {
  if (filter === 'all') return true
  if (filter === 'errors') {
    return event.severity?.toLowerCase() === 'error'
  }
  const type = event.type?.toLowerCase() ?? ''
  switch (filter) {
    case 'boot':
      return (
        type.startsWith('bootstrap') ||
        type.startsWith('install') ||
        type.startsWith('setup') ||
        type.startsWith('specdelta') ||
        type.startsWith('specvalidation') ||
        type.startsWith('specapply')
      )
    case 'services':
      return type.startsWith('service')
    case 'health':
      return (
        type.startsWith('heartbeat') ||
        type.startsWith('disk') ||
        type.startsWith('runtimerespawn') ||
        type.startsWith('cloudflared') ||
        type.startsWith('runtimefly')
      )
    default:
      return true
  }
}

/**
 * Coarse type bucket used to colour-code the left border of a Timeline row.
 * Greener for healthy service transitions, red for crashes, blue for boot
 * progress, grey for heartbeats. Falls through to {@code other} so an
 * unrecognised event renders without an accent rather than a default tone.
 */
export type TimelineRowAccent =
  | 'boot'
  | 'service-healthy'
  | 'service-crash'
  | 'heartbeat'
  | 'error'
  | 'other'

export function rowAccent(event: RuntimeEventDto): TimelineRowAccent {
  const type = event.type ?? ''
  const lower = type.toLowerCase()
  const severity = event.severity?.toLowerCase() ?? ''

  // Severity Error wins — even a "ServiceRestarted" with severity=Error reads
  // as a problem.
  if (severity === 'error') return 'error'
  if (
    type === 'SpecValidationFailed' ||
    type === 'SpecApplyAckFailed' ||
    type === 'DiskPressureCritical' ||
    type === 'BootstrapStageFailed' ||
    type === 'InstallFailed' ||
    type === 'SetupCommandFailed' ||
    type === 'SpecDeltaFailed' ||
    type === 'RuntimeRespawnTriggered'
  ) {
    return 'error'
  }
  if (lower.startsWith('service')) {
    if (
      type === 'ServiceCrashed' ||
      type === 'ServiceFailedToStart' ||
      type === 'ServiceFatal'
    ) {
      return 'service-crash'
    }
    // Healthcheck advisory rows: Warn severity (timed out) lands on heartbeat
    // (muted, distinct from the healthy green); Info (probe-failed) likewise
    // reads as quiet diagnostic noise rather than success.
    if (
      type === 'ServiceHealthcheckTimedOut' ||
      type === 'ServiceHealthcheckProbeFailed'
    ) {
      return 'heartbeat'
    }
    return 'service-healthy'
  }
  if (
    lower.startsWith('bootstrap') ||
    lower.startsWith('install') ||
    lower.startsWith('setup') ||
    lower.startsWith('specdelta')
  ) {
    return 'boot'
  }
  if (lower.startsWith('heartbeat')) return 'heartbeat'
  return 'other'
}

/**
 * Convert CamelCase event types (e.g. {@code InstallCompleted}) into a
 * sentence-cased, humanised string (e.g. "Install completed") so the Timeline
 * row reads at a glance.
 */
export function humanizeEventType(type: string): string {
  if (!type) return 'Event'
  const withSpaces = type.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  return (
    withSpaces.charAt(0).toUpperCase() + withSpaces.slice(1).toLowerCase()
  )
}

/**
 * Render a duration as a human-friendly string with appropriate precision.
 * Sub-second values get one decimal of seconds (so "0.8s" reads as fast),
 * single-digit seconds keep one decimal ("47.3s"), and longer durations roll
 * up to minutes / hours to keep the row scannable.
 */
export function formatDuration(durationMs: number | null | undefined): string {
  if (durationMs == null || !Number.isFinite(durationMs)) return ''
  if (durationMs < 0) return ''
  if (durationMs < 1000) {
    // Sub-second: keep one decimal in seconds so "850ms" reads as "0.9s",
    // matching the rest of the column. Integer ms creates a noisy mismatch.
    return `${(durationMs / 1000).toFixed(1)}s`
  }
  const seconds = durationMs / 1000
  if (seconds < 60) return `${seconds.toFixed(1)}s`
  const minutes = Math.floor(seconds / 60)
  const remSeconds = Math.round(seconds % 60)
  if (minutes < 60) return `${minutes}m ${remSeconds}s`
  const hours = Math.floor(minutes / 60)
  const remMinutes = minutes % 60
  return `${hours}h ${remMinutes}m`
}

/**
 * Map a {@code durationMs} to the semantic MUI palette key that flags slow
 * operations. Returns {@code null} for events whose duration is fast or
 * absent — the caller renders those in the default text colour.
 *
 * <p>Thresholds are intentionally aggressive (5s yellow, 30s red) so the user
 * notices regressions without having to know baselines. This mirrors the
 * "Timing — first-class concern" section of the runtime-spec-v2 spec.</p>
 */
export function durationColorKey(
  durationMs: number | null | undefined,
): 'warning.main' | 'error.main' | null {
  if (durationMs == null || !Number.isFinite(durationMs)) return null
  if (durationMs >= 30_000) return 'error.main'
  if (durationMs >= 5_000) return 'warning.main'
  return null
}

/**
 * Compact "3s ago" / "2m ago" rendering for the Timeline column. Falls back
 * to a localised date string for anything older than a day so absolute time
 * stays available for incident forensics.
 */
export function formatRelativeTime(
  timestamp: string | Date | null | undefined,
  now: Date = new Date(),
): string {
  if (!timestamp) return ''
  const ts = typeof timestamp === 'string' ? new Date(timestamp) : timestamp
  if (Number.isNaN(ts.getTime())) return ''
  const diffMs = now.getTime() - ts.getTime()
  if (diffMs < 0) return 'just now'
  const seconds = Math.floor(diffMs / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return ts.toLocaleString()
}

/**
 * Best-effort JSON decode of the raw payload string. The REST DTO declares
 * payload as {@code unknown} (string from the server, sometimes an object
 * after Orval round-trips); SignalR delivers it as a string. We tolerate
 * either and never throw — bad JSON returns null so the UI can fall back to
 * the bare event type instead of erroring.
 */
export function parseEventPayload(payload: unknown): Record<string, unknown> | null {
  if (payload == null) return null
  if (typeof payload === 'object') {
    return payload as Record<string, unknown>
  }
  if (typeof payload !== 'string') return null
  const trimmed = payload.trim()
  if (trimmed.length === 0) return null
  try {
    const parsed = JSON.parse(trimmed)
    return typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>)
      : null
  } catch {
    return null
  }
}

/**
 * Extract a service name from the event payload. Used by the Services tab to
 * derive per-service state from the event stream. Returns null when the
 * event isn't service-related or the payload doesn't carry a name field.
 */
export function getServiceName(event: RuntimeEventDto): string | null {
  if (!event.type?.toLowerCase().startsWith('service')) return null
  const payload = parseEventPayload(event.payload)
  if (!payload) return null
  const name = payload['serviceName'] ?? payload['name']
  return typeof name === 'string' && name.length > 0 ? name : null
}
