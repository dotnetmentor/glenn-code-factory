import { useCallback, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Collapse,
  IconButton,
  Snackbar,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import BoltIcon from '@mui/icons-material/Bolt'
import FavoriteBorderIcon from '@mui/icons-material/FavoriteBorder'
import type { SvgIconComponent } from '@mui/icons-material'
import type { RuntimeEventDto } from '@/api/queries-commands'
import {
  monoNumberSx,
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { IconTile, type IconTileTone } from '@/applications/workspace/shared/primitives'
import {
  durationColorKey,
  formatDuration,
  formatRelativeTime,
  humanizeEventType,
  matchesFilter,
  parseEventPayload,
  rowAccent,
  TIMELINE_FILTERS,
  type TimelineFilter,
  type TimelineRowAccent,
} from '../utils/runtimeEventDisplay'
import {
  groupTimelineEvents,
  type TimelineGroup,
} from '../utils/groupTimelineEvents'
import { BootTimingSummary } from './BootTimingSummary'

// TODO(timeline-state-merge): Defer — surface RuntimeStateEvent rows when
// backend endpoint lands. Tracked separately.

/**
 * Left-border accent colours per row bucket. Keyed off {@link TimelineRowAccent}.
 * Tones lifted from the workspace runtime palette so the rail reads as part
 * of the same family instead of MUI semantic alert paint. Boot rows ride the
 * accent ink (the prototype's `t.accent`) since the palette has no blue token.
 */
const ACCENT_COLOURS: Record<TimelineRowAccent, string> = {
  boot: workspaceAccent.ink,
  'service-healthy': workspaceRuntime.online,
  'service-crash': workspaceRuntime.failed,
  heartbeat: workspaceText.faint,
  error: workspaceRuntime.failed,
  other: 'transparent',
}

/**
 * Maps a row's {@link TimelineRowAccent} bucket onto the shared {@link IconTile}
 * tone + a representative glyph, matching the prototype's per-row 22px tone
 * tiles (green check / amber bolt / red error / muted heartbeat). Pure
 * presentation — derived from the same {@code rowAccent} bucket that already
 * drives the left rail, so the tile and rail always read as one family.
 */
function tileForAccent(accent: TimelineRowAccent): {
  tone: IconTileTone
  icon: SvgIconComponent
} {
  switch (accent) {
    case 'error':
    case 'service-crash':
      return { tone: 'err', icon: ErrorOutlineIcon }
    case 'service-healthy':
      return { tone: 'ok', icon: CheckCircleOutlineIcon }
    case 'boot':
      return { tone: 'warn', icon: BoltIcon }
    case 'heartbeat':
      return { tone: 'mute', icon: FavoriteBorderIcon }
    case 'other':
    default:
      return { tone: 'mute', icon: InfoOutlinedIcon }
  }
}

/**
 * Refine the per-row tile tone using the event's own severity, so a "warn"
 * heartbeat (e.g. a healthcheck timeout) reads amber while a plain heartbeat
 * stays muted. Severity wins over the coarse bucket tone for warn/err; ok/mute
 * fall back to the bucket mapping. Pure presentation.
 */
function tileToneForEvent(
  accent: TimelineRowAccent,
  severity: string | null | undefined,
): IconTileTone {
  const sev = severity?.toLowerCase()
  if (sev === 'error') return 'err'
  if (sev === 'warn' || sev === 'warning') return 'warn'
  return tileForAccent(accent).tone
}

/**
 * The Timeline tab of the runtime drawer. Renders a reverse-chronological
 * list of structured runtime events with severity icons, relative
 * timestamps, humanised type names, and — most importantly — durations
 * inline with color-coding for slow operations.
 *
 * <p>Visually tuned to the workspace surface — gentle filter pill chips
 * (matches the debug-panel segmented switcher), muted severity icons,
 * hairline row dividers, and SFMono durations with a quiet bronze tint
 * for slow operations rather than MUI semantic red/yellow.</p>
 */
export interface TimelineTabProps {
  events: RuntimeEventDto[]
  hasMore: boolean
  loadingInitial: boolean
  loadingMore: boolean
  error: unknown
  onLoadMore: () => void
}

export function TimelineTab(props: TimelineTabProps) {
  const { events, hasMore, loadingInitial, loadingMore, error, onLoadMore } =
    props
  const [filter, setFilter] = useState<TimelineFilter>('all')
  const [copyToastOpen, setCopyToastOpen] = useState(false)

  const filteredEvents = useMemo(
    () => events.filter((e) => matchesFilter(e, filter)),
    [events, filter],
  )

  // Per-filter counts for the pill badges (prototype shows a tabular-mono
  // count beside each label). Derived purely from the loaded `events` — this
  // does NOT change filtering: the active filter still drives `filteredEvents`
  // above; these are display-only tallies of how many loaded events each
  // bucket would match.
  const filterCounts = useMemo(() => {
    const counts = {} as Record<TimelineFilter, number>
    for (const chip of TIMELINE_FILTERS) {
      counts[chip.value] = events.filter((e) =>
        matchesFilter(e, chip.value),
      ).length
    }
    return counts
  }, [events])

  // Group consecutive identical events (e.g. 791× ServiceStarting). Pure
  // function — memo'd on the filtered list reference.
  const grouped = useMemo(
    () => groupTimelineEvents(filteredEvents),
    [filteredEvents],
  )

  const handleCopy = useCallback(() => {
    setCopyToastOpen(true)
  }, [])

  return (
    <Stack spacing={2} sx={{ height: '100%' }}>
      <BootTimingSummary events={events} />

      {/* Filter pill chips — workspace vocabulary, not MUI primary fill. */}
      <Stack
        direction="row"
        role="tablist"
        aria-label="Timeline filter"
        spacing={0.5}
        flexWrap="wrap"
        useFlexGap
        sx={{ rowGap: 0.5 }}
      >
        {TIMELINE_FILTERS.map((chip) => {
          const active = filter === chip.value
          const isErrors = chip.value === 'errors'
          const count = filterCounts[chip.value] ?? 0
          // Inactive count tint: errors bucket nudges to the failed tone so a
          // non-zero error tally reads at a glance; everything else stays faint.
          const countColor = active
            ? workspaceColors.canvasBg
            : isErrors && count > 0
              ? workspaceRuntime.failed
              : workspaceText.faint
          return (
            <Box
              key={chip.value}
              component="button"
              type="button"
              role="tab"
              aria-selected={active}
              onClick={() => setFilter(chip.value)}
              sx={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 0.625,
                outline: 0,
                cursor: 'pointer',
                px: 1.25,
                py: 0.5,
                fontSize: '0.71875rem',
                fontWeight: 500,
                letterSpacing: '-0.005em',
                borderRadius: 999,
                border: `1px solid ${active ? workspaceAccent.ink : workspaceColors.hairline}`,
                bgcolor: active ? workspaceAccent.ink : 'transparent',
                color: active ? workspaceColors.canvasBg : workspaceText.muted,
                transition: 'background-color 160ms ease, color 160ms ease, border-color 160ms ease',
                '&:hover': {
                  color: active ? workspaceColors.canvasBg : workspaceText.primary,
                  borderColor: active ? workspaceAccent.ink : workspaceText.faint,
                },
                '&:focus-visible': {
                  outline: `2px solid ${workspaceAccent.ink}`,
                  outlineOffset: 1,
                },
              }}
            >
              <Box component="span">{chip.label}</Box>
              <Box
                component="span"
                sx={{
                  ...monoNumberSx,
                  fontSize: '0.65625rem',
                  color: countColor,
                  opacity: active ? 0.8 : 1,
                }}
              >
                {count}
              </Box>
            </Box>
          )
        })}
      </Stack>

      {error ? (
        <Alert severity="error">
          Failed to load runtime events. Refresh to try again.
        </Alert>
      ) : null}

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        {loadingInitial ? (
          <Stack
            direction="row"
            spacing={1}
            alignItems="center"
            sx={{ py: 4, justifyContent: 'center' }}
          >
            <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
            <Typography sx={{ fontSize: 13, color: workspaceText.muted }}>
              Loading events…
            </Typography>
          </Stack>
        ) : filteredEvents.length === 0 ? (
          <EmptyTimelineState
            hasAnyEvent={events.length > 0}
            activeFilter={filter}
          />
        ) : (
          <Stack spacing={0}>
            {grouped.map((entry) =>
              entry.kind === 'single' ? (
                <TimelineRow
                  key={entry.event.id}
                  event={entry.event}
                  onCopy={handleCopy}
                />
              ) : (
                // Key on `earliest.id` (oldest event in the run, anchor that
                // doesn't move when newer events join the group at the front).
                // Previously this used `latest.id-idx` which rotated on every
                // new event landing in the group — that remounted the row,
                // tore down the local `useState(open)`, and snapped any
                // expanded payload shut while the user was reading it.
                <TimelineGroupRow
                  key={`group-${entry.earliest.id}`}
                  group={entry}
                  onCopy={handleCopy}
                />
              ),
            )}
            <Box sx={{ mt: 1.5, display: 'flex', justifyContent: 'center' }}>
              {hasMore ? (
                <Button
                  size="small"
                  onClick={onLoadMore}
                  disabled={loadingMore}
                  startIcon={
                    loadingMore ? (
                      <CircularProgress
                        size={12}
                        sx={{ color: workspaceText.muted }}
                      />
                    ) : undefined
                  }                >
                  {loadingMore ? 'Loading…' : 'Load older events'}
                </Button>
              ) : events.length > 0 ? (
                <Typography sx={{ fontSize: 12, color: workspaceText.faint }}>
                  End of history
                </Typography>
              ) : null}
            </Box>
          </Stack>
        )}
      </Box>

      <Snackbar
        open={copyToastOpen}
        autoHideDuration={1800}
        onClose={() => setCopyToastOpen(false)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        // Low z-index + muted paper toast — matches the warm-paper palette
        // and never competes with the workspace shell's own affordances.
        sx={{ zIndex: 1 }}
        ContentProps={{
          sx: {
            bgcolor: workspaceColors.chromeBg,
            color: workspaceText.primary,
            fontSize: 12.5,
            fontFamily: workspaceFontFamily.sans,
            letterSpacing: '-0.005em',
            boxShadow: '0 4px 14px rgba(0,0,0,0.08)',
            border: `1px solid ${workspaceColors.hairline}`,
            borderRadius: 1.25,
            minWidth: 0,
            px: 1.5,
            py: 0.75,
          },
        }}
        message="Copied to clipboard"
      />
    </Stack>
  )
}

function EmptyTimelineState({
  hasAnyEvent,
  activeFilter,
}: {
  hasAnyEvent: boolean
  activeFilter: TimelineFilter
}) {
  if (hasAnyEvent && activeFilter !== 'all') {
    return (
      <Typography
        sx={{ fontSize: 13, color: workspaceText.muted, py: 4, textAlign: 'center' }}
      >
        No events match the “{activeFilter}” filter.
      </Typography>
    )
  }
  return (
    <Typography
      sx={{ fontSize: 13, color: workspaceText.muted, py: 4, textAlign: 'center' }}
    >
      No runtime events yet. They appear here as the daemon boots services and
      applies spec changes.
    </Typography>
  )
}

/**
 * Best-effort clipboard write. Swallows errors silently — the user sees no
 * toast on failure, which is the correct affordance for a clipboard-denied
 * permission state.
 */
async function writeClipboard(text: string): Promise<boolean> {
  try {
    if (
      typeof navigator !== 'undefined' &&
      navigator.clipboard &&
      typeof navigator.clipboard.writeText === 'function'
    ) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // ignored — clipboard permission denied or insecure context
  }
  return false
}

function CopyRowButton({
  onCopy,
}: {
  onCopy: (e: React.MouseEvent) => void
}) {
  return (
    <Tooltip title="Copy JSON" placement="left" arrow>
      <IconButton
        size="small"
        className="timeline-row-copy"
        aria-label="Copy event JSON"
        onClick={onCopy}
        sx={{
          p: 0.25,
          color: workspaceText.faint,
          opacity: 0,
          transition: 'opacity 120ms ease, color 120ms ease',
          '&:hover': { color: workspaceText.muted },
          '&:focus-visible': { opacity: 1 },
        }}
      >
        <ContentCopyIcon sx={{ fontSize: 16 }} />
      </IconButton>
    </Tooltip>
  )
}

function TimelineRow({
  event,
  onCopy,
}: {
  event: RuntimeEventDto
  onCopy: () => void
}) {
  const durationStr = formatDuration(event.durationMs)
  const durationColor = durationColorKey(event.durationMs)
  const relative = formatRelativeTime(event.timestamp)
  const accent = rowAccent(event)
  const accentColor = ACCENT_COLOURS[accent]
  const tileIcon = tileForAccent(accent).icon
  const tileTone = tileToneForEvent(accent, event.severity)

  // Map MUI semantic keys to our muted workspace runtime palette so durations
  // never feel like alert paint. Slow ops nudge toward bronze; very slow ops
  // toward the failed tone — both quieter than MUI's warning.main / error.main.
  let durationTint: string | undefined
  if (durationColor === 'warning.main') durationTint = workspaceRuntime.booting
  if (durationColor === 'error.main') durationTint = workspaceRuntime.failed

  // Payload-expand state. Only rows whose payload parses to a non-empty
  // object render the toggle — events with null / empty payloads stay as
  // simple one-liners.
  const parsedPayload = useMemo(
    () => parseEventPayload(event.payload),
    [event.payload],
  )
  // `BootstrapOutputChunk` (install/setup bash) and `ServiceOutputChunk`
  // (per-service stdout/stderr captured during the starting-services window)
  // both need the live-streamed-output row treatment — collapsed by default
  // with a `{label}` mono header plus a "N lines" badge, expanded to a
  // monospaced pre block with the raw lines. The extractor returns a unified
  // shape so the renderer below doesn't have to branch on event type.
  const chunkInfo = useMemo(
    () => extractOutputChunk(event, parsedPayload),
    [event, parsedPayload],
  )
  // `ServiceHealthcheckTimedOut` / `ServiceHealthcheckProbeFailed` carry the
  // probe's captured stdout/stderr tails. Same expandable-mono treatment as
  // the chunk renderer but with a single label + the tails stacked in the
  // expanded body.
  const healthcheckInfo = useMemo(
    () => extractHealthcheckPayload(event, parsedPayload),
    [event, parsedPayload],
  )
  // `ServiceHealthy` has a tiny dedicated label so the row reads as
  // "{name} healthy ({durationMs}ms)" instead of the verbose humanised
  // "Service healthy" + a duration chip. No expanded body — payload is small
  // enough to live entirely in the label.
  const healthyInfo = useMemo(
    () => extractServiceHealthy(event, parsedPayload),
    [event, parsedPayload],
  )
  const hasPayload = parsedPayload != null && Object.keys(parsedPayload).length > 0
  const [open, setOpen] = useState(false)

  const handleCopyClick = useCallback(
    async (e: React.MouseEvent) => {
      e.stopPropagation()
      const ok = await writeClipboard(
        JSON.stringify(
          {
            type: event.type,
            occurredAt: event.timestamp,
            payload: parsedPayload ?? event.payload,
          },
          null,
          2,
        ),
      )
      if (ok) onCopy()
    },
    [event.type, event.timestamp, event.payload, parsedPayload, onCopy],
  )

  return (
    <Box
      sx={{
        // 4px solid left border keyed to the event bucket. Transparent for
        // "other" so unrecognised types don't gain visual weight.
        borderLeft: `4px solid ${accentColor}`,
        pl: 1.25,
        borderBottom: `1px solid ${workspaceColors.hairline}`,
        '&:last-of-type': { borderBottom: 0 },
        py: 0.5,
        // Faint tint for streamed-output chunks AND probe-detail rows so they
        // read as distinct from structured bookend events at a glance without
        // competing for attention.
        bgcolor:
          chunkInfo || healthcheckInfo
            ? `${workspaceColors.codeBg}66`
            : 'transparent',
        // Parent-hover selector reveals the per-row Copy affordance.
        '&:hover .timeline-row-copy': { opacity: 1 },
      }}
    >
      <Stack
        direction="row"
        spacing={1.25}
        alignItems="flex-start"
        sx={{
          py: 0.5,
          cursor: hasPayload ? 'pointer' : 'default',
        }}
        onClick={hasPayload ? () => setOpen((v) => !v) : undefined}
        role={hasPayload ? 'button' : undefined}
        aria-expanded={hasPayload ? open : undefined}
        aria-label={hasPayload ? 'Toggle event payload' : undefined}
      >
        <Box sx={{ pt: 0.125 }}>
          <IconTile icon={tileIcon} tone={tileTone} />
        </Box>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack direction="row" spacing={1} alignItems="baseline" flexWrap="wrap">
            <Typography
              sx={{
                fontSize: 13,
                fontWeight: 500,
                color: healthyInfo
                  ? workspaceRuntime.online
                  : workspaceText.primary,
                letterSpacing: '-0.005em',
                // Output chunks + healthcheck rows get a mono label since the
                // identifier form is path-shaped (`service:foo/stdout`,
                // `foo exit=7`); the humanised sentence form would just be
                // visual noise on every line.
                fontFamily:
                  chunkInfo || healthcheckInfo
                    ? workspaceFontFamily.mono
                    : undefined,
              }}
            >
              {chunkInfo
                ? chunkInfo.label
                : healthcheckInfo
                  ? healthcheckInfo.label
                  : healthyInfo
                    ? healthyInfo.label
                    : humanizeEventType(event.type)}
            </Typography>
            {chunkInfo && (
              <Box
                component="span"
                sx={{
                  ...monoNumberSx,
                  fontSize: 10.5,
                  color: workspaceText.muted,
                  fontWeight: 500,
                  px: 0.75,
                  py: 0.125,
                  borderRadius: '4px',
                  bgcolor: workspaceColors.chipBg,
                }}
              >
                {`${chunkInfo.lineCount} line${chunkInfo.lineCount === 1 ? '' : 's'}`}
              </Box>
            )}
            {!chunkInfo && !healthcheckInfo && !healthyInfo && durationStr && (
              <Box
                component="span"
                sx={{
                  ...monoNumberSx,
                  fontSize: 10.5,
                  color: durationTint ?? workspaceText.faint,
                  fontWeight: durationTint ? 600 : 500,
                  px: 0.75,
                  py: 0.125,
                  borderRadius: '4px',
                  bgcolor: durationTint
                    ? `${durationTint}14`
                    : workspaceColors.chipBg,
                }}
              >
                {durationStr}
              </Box>
            )}
          </Stack>
          <Typography
            sx={{
              fontSize: 11.5,
              color: workspaceText.faint,
              fontFamily: workspaceFontFamily.mono,
              mt: 0.25,
            }}
          >
            {relative}
          </Typography>
        </Box>
        <CopyRowButton onCopy={handleCopyClick} />
        {hasPayload && (
          <IconButton
            size="small"
            sx={{ p: 0.25, color: workspaceText.faint }}
            aria-hidden
            tabIndex={-1}
            // The whole row is the click target; this is just the affordance.
            onClick={(e) => {
              e.stopPropagation()
              setOpen((v) => !v)
            }}
          >
            {open ? (
              <ExpandLessIcon sx={{ fontSize: 16 }} />
            ) : (
              <ExpandMoreIcon sx={{ fontSize: 16 }} />
            )}
          </IconButton>
        )}
      </Stack>
      {hasPayload && (
        <Collapse in={open} unmountOnExit>
          <Box
            sx={{
              mt: 0.5,
              mb: 0.75,
              ml: 4,
              mr: 1,
              p: 1,
              maxHeight: 260,
              overflow: 'auto',
              bgcolor: workspaceColors.codeBg,
              border: `1px solid ${workspaceColors.codeBorder}`,
              borderRadius: 1,
            }}
          >
            <Typography
              component="pre"
              sx={{
                m: 0,
                fontFamily: workspaceFontFamily.mono,
                fontSize: 11.5,
                color: workspaceText.primary,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
              }}
            >
              {chunkInfo
                ? chunkInfo.lines.join('\n')
                : healthcheckInfo
                  ? renderHealthcheckBody(healthcheckInfo)
                  : JSON.stringify(parsedPayload, null, 2)}
            </Typography>
          </Box>
        </Collapse>
      )}
    </Box>
  )
}

/**
 * Pluck the streamed-output payload fields off a runtime event, if it's one of
 * the chunk types: `BootstrapOutputChunk` (install/setup stages) or
 * `ServiceOutputChunk` (per-service tail during starting-services). Returns
 * null for any other event type — the caller uses the presence/absence of
 * this object to switch the row render between the generic JSON-dump path and
 * the monospaced lines path.
 *
 * <p>Both payload shapes carry `{ stream, lines, lineCount, ... }`. The
 * difference is the identifier prefix: BootstrapOutputChunk carries
 * `{ stage: "install"|"setup" }`, ServiceOutputChunk carries
 * `{ serviceName }`. We render a single `label` here so the row component
 * doesn't have to branch on event type.</p>
 *
 * <p>We re-validate the shape client-side because the wire is plain JSON and
 * we never want a malformed payload to crash a Timeline render — anything
 * off-shape falls back to the generic path.</p>
 */
function extractOutputChunk(
  event: RuntimeEventDto,
  parsedPayload: Record<string, unknown> | null,
): { label: string; lineCount: number; lines: string[] } | null {
  if (
    event.type !== 'BootstrapOutputChunk' &&
    event.type !== 'ServiceOutputChunk'
  ) {
    return null
  }
  if (!parsedPayload) return null
  const stream = parsedPayload['stream']
  const rawLines = parsedPayload['lines']
  if (typeof stream !== 'string') return null
  if (!Array.isArray(rawLines)) return null
  const lines = rawLines.filter((l): l is string => typeof l === 'string')
  const rawCount = parsedPayload['lineCount']
  const lineCount =
    typeof rawCount === 'number' && Number.isFinite(rawCount)
      ? rawCount
      : lines.length

  let label: string
  if (event.type === 'BootstrapOutputChunk') {
    const stage = parsedPayload['stage']
    if (typeof stage !== 'string') return null
    label = `${stage}/${stream}`
  } else {
    const serviceName = parsedPayload['serviceName']
    if (typeof serviceName !== 'string') return null
    label = `service:${serviceName}/${stream}`
  }
  return { label, lineCount, lines }
}

/**
 * Pluck the `ServiceHealthcheckTimedOut` / `ServiceHealthcheckProbeFailed`
 * payload off a runtime event. Both carry an exit code + the captured
 * stdout/stderr tails from the failing probe; the renderer uses them for the
 * expanded body. Returns null for any other event type.
 *
 * <p>Format choices:
 *   <ul>
 *     <li><code>ServiceHealthcheckTimedOut</code>: label =
 *         "<i>service</i> healthcheck timed out — exit <i>code</i>".</li>
 *     <li><code>ServiceHealthcheckProbeFailed</code>: label =
 *         "<i>service</i> probe exit=<i>code</i> (attempt <i>N</i>)" — the
 *         attempt number is load-bearing here because the daemon emits one
 *         per state-transition / every 5th probe and the operator needs to
 *         know which iteration's tails they're looking at.</li>
 *   </ul></p>
 */
function extractHealthcheckPayload(
  event: RuntimeEventDto,
  parsedPayload: Record<string, unknown> | null,
): {
  label: string
  stdoutTail: string
  stderrTail: string
  attemptCount: number | null
  exitCode: number | null
} | null {
  if (
    event.type !== 'ServiceHealthcheckTimedOut' &&
    event.type !== 'ServiceHealthcheckProbeFailed'
  ) {
    return null
  }
  if (!parsedPayload) return null
  const serviceName = parsedPayload['serviceName']
  if (typeof serviceName !== 'string') return null

  const exitCodeRaw =
    event.type === 'ServiceHealthcheckTimedOut'
      ? parsedPayload['lastExitCode']
      : parsedPayload['exitCode']
  const exitCode =
    typeof exitCodeRaw === 'number' && Number.isFinite(exitCodeRaw)
      ? exitCodeRaw
      : null

  const stdoutKey =
    event.type === 'ServiceHealthcheckTimedOut'
      ? 'lastStdoutTail'
      : 'stdoutTail'
  const stderrKey =
    event.type === 'ServiceHealthcheckTimedOut'
      ? 'lastStderrTail'
      : 'stderrTail'
  const stdoutTail =
    typeof parsedPayload[stdoutKey] === 'string'
      ? (parsedPayload[stdoutKey] as string)
      : ''
  const stderrTail =
    typeof parsedPayload[stderrKey] === 'string'
      ? (parsedPayload[stderrKey] as string)
      : ''

  const attemptRaw = parsedPayload['attemptCount']
  const attemptCount =
    typeof attemptRaw === 'number' && Number.isFinite(attemptRaw)
      ? attemptRaw
      : null

  let label: string
  if (event.type === 'ServiceHealthcheckTimedOut') {
    label = `${serviceName} healthcheck timed out — exit ${exitCode ?? '?'}`
  } else {
    const attemptPart = attemptCount !== null ? ` (attempt ${attemptCount})` : ''
    label = `${serviceName} probe exit=${exitCode ?? '?'}${attemptPart}`
  }
  return { label, stdoutTail, stderrTail, attemptCount, exitCode }
}

/**
 * Pluck the `ServiceHealthy` payload off a runtime event. The label folds
 * everything into a single sentence: "{serviceName} healthy ({durationMs}ms)"
 * — the payload is small enough that an expanded JSON body would just dilute
 * the signal. Returns null for any other event type.
 */
function extractServiceHealthy(
  event: RuntimeEventDto,
  parsedPayload: Record<string, unknown> | null,
): { label: string } | null {
  if (event.type !== 'ServiceHealthy') return null
  if (!parsedPayload) return null
  const serviceName = parsedPayload['serviceName']
  if (typeof serviceName !== 'string') return null
  const durationRaw = parsedPayload['durationMs']
  const durationMs =
    typeof durationRaw === 'number' && Number.isFinite(durationRaw)
      ? durationRaw
      : null
  const durationPart = durationMs !== null ? ` (${durationMs}ms)` : ''
  return { label: `${serviceName} healthy${durationPart}` }
}

/**
 * Render the expanded body of a healthcheck probe row: stdout tail, then a
 * separator, then stderr tail. Empty sections collapse to "(empty)" so the
 * operator can tell apart "the probe produced nothing" from "the field was
 * missing entirely".
 */
function renderHealthcheckBody(info: {
  stdoutTail: string
  stderrTail: string
}): string {
  const stdoutSection = info.stdoutTail.length > 0 ? info.stdoutTail : '(empty)'
  const stderrSection = info.stderrTail.length > 0 ? info.stderrTail : '(empty)'
  return `--- stdout ---\n${stdoutSection}\n\n--- stderr ---\n${stderrSection}`
}

/**
 * Render a collapsed run of N identical events as a single row. Visually
 * identical to a singleton row but carries an inline "× N" count badge and
 * a secondary timestamp line showing the earliest → latest span. Click to
 * expand and reveal the underlying singletons inline.
 */
function TimelineGroupRow({
  group,
  onCopy,
}: {
  group: Extract<TimelineGroup, { kind: 'group' }>
  onCopy: () => void
}) {
  const [open, setOpen] = useState(false)
  // Mirror the first event's accent so the rail stays consistent with how a
  // singleton occurrence of the same type would have read.
  const accent = rowAccent(group.latest)
  const accentColor = ACCENT_COLOURS[accent]
  const tileIcon = tileForAccent(accent).icon
  const tileTone = tileToneForEvent(accent, group.latest.severity)
  const relative = formatRelativeTime(group.latest.timestamp)
  const earliestRel = formatRelativeTime(group.earliest.timestamp)

  const handleCopyClick = useCallback(
    async (e: React.MouseEvent) => {
      e.stopPropagation()
      const ok = await writeClipboard(
        JSON.stringify(
          group.events.map((ev) => ({
            type: ev.type,
            occurredAt: ev.timestamp,
            payload: parseEventPayload(ev.payload) ?? ev.payload,
          })),
          null,
          2,
        ),
      )
      if (ok) onCopy()
    },
    [group.events, onCopy],
  )

  return (
    <Box
      sx={{
        borderLeft: `4px solid ${accentColor}`,
        pl: 1.25,
        borderBottom: `1px solid ${workspaceColors.hairline}`,
        '&:last-of-type': { borderBottom: 0 },
        py: 0.5,
        '&:hover .timeline-row-copy': { opacity: 1 },
      }}
    >
      <Stack
        direction="row"
        spacing={1.25}
        alignItems="flex-start"
        sx={{ py: 0.5, cursor: 'pointer' }}
        onClick={() => setOpen((v) => !v)}
        role="button"
        aria-expanded={open}
        aria-label={`Toggle ${group.count} grouped events`}
      >
        <Box sx={{ pt: 0.125 }}>
          <IconTile icon={tileIcon} tone={tileTone} />
        </Box>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack direction="row" spacing={1} alignItems="baseline" flexWrap="wrap">
            <Typography
              sx={{
                fontSize: 13,
                fontWeight: 500,
                color: workspaceText.primary,
                letterSpacing: '-0.005em',
              }}
            >
              {humanizeEventType(group.type)}
            </Typography>
            <Box
              component="span"
              sx={{
                ...monoNumberSx,
                fontSize: 10.5,
                color: workspaceAccent.ink,
                fontWeight: 600,
                px: 0.75,
                py: 0.125,
                borderRadius: '4px',
                bgcolor: workspaceColors.chipBg,
              }}
            >
              {`× ${group.count}`}
            </Box>
          </Stack>
          <Tooltip
            title={`${earliestRel} → ${relative}`}
            placement="bottom-start"
            arrow
          >
            <Typography
              sx={{
                fontSize: 11.5,
                color: workspaceText.faint,
                fontFamily: workspaceFontFamily.mono,
                mt: 0.25,
              }}
            >
              {`${earliestRel} → ${relative}`}
            </Typography>
          </Tooltip>
        </Box>
        <CopyRowButton onCopy={handleCopyClick} />
        <IconButton
          size="small"
          sx={{ p: 0.25, color: workspaceText.faint }}
          aria-hidden
          tabIndex={-1}
          onClick={(e) => {
            e.stopPropagation()
            setOpen((v) => !v)
          }}
        >
          {open ? (
            <ExpandLessIcon sx={{ fontSize: 16 }} />
          ) : (
            <ExpandMoreIcon sx={{ fontSize: 16 }} />
          )}
        </IconButton>
      </Stack>
      <Collapse in={open} unmountOnExit>
        <Box sx={{ mt: 0.25, mb: 0.5, ml: 2 }}>
          <Stack spacing={0}>
            {group.events.map((ev) => (
              <TimelineRow key={ev.id} event={ev} onCopy={onCopy} />
            ))}
          </Stack>
        </Box>
      </Collapse>
    </Box>
  )
}
