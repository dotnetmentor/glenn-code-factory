/**
 * TurnPhaseChrome — composes the new Cursor-native activity surfaces for one
 * turn phase (cursor-native-chat-ux, card 7 composition).
 *
 * <p>Replaces the old {@code TurnStatusLine} + {@code TurnTrace} pair with:</p>
 * <ul>
 *   <li>An {@link ActivityPill} anchored at the top, derived from the
 *       phase's {@link Message[]} via {@link deriveActivityPillState}.</li>
 *   <li>An optional inline thinking preview beneath the pill (scene 4).</li>
 *   <li>A {@link TurnFooter} below the bubbles when the phase is terminal
 *       (scene 6) — only on the LAST phase, so older closed phases keep
 *       their compact pill summary without a duplicate footer.</li>
 *   <li>The expanded {@link TurnTrace} when the user clicks the pill chevron.</li>
 * </ul>
 *
 * <p>One phase = one piece of the turn between consecutive assistant
 * messages. The phase's events are filtered to its slice of the wire
 * stream by {@code splitTurnIntoPhases} in ChatCanvas.</p>
 */
import { useMemo, useState } from 'react'
import { Box, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import type { AgentEventDto, RunResultDto } from '../../../../../api/queries-commands'
import {
  ActivityPill,
  type ActivityPillState,
  useElapsedMs,
} from './ActivityPill'
import { TurnTrace } from './TurnTrace'
import { TurnFooter, type TerminalRunStatus } from './TurnFooter'
import { eventToMessage, isStatus, isThinking, type Message } from './chatEvents'
import { deriveActivityPillState } from './deriveActivityPillState'
import { workspaceText, surfaceTokens } from '../../../shared/designTokens'

// ── Public props ────────────────────────────────────────────────────────────

export interface TurnPhaseChromeProps {
  /** Stable id for {@code data-testid} disambiguation. */
  sessionId: string
  /** Events scoped to THIS phase, in chronological order. */
  events: AgentEventDto[]
  /** Whether this phase is the latest, still-streaming one. */
  isLiveTurn: boolean
  /**
   * Whether this is the LAST phase in the turn. Footers only render on the
   * last phase — older closed phases keep their pill summary only.
   */
  isLastPhase: boolean
  /**
   * Terminal run status if the turn has ended ({@code Finished} / {@code
   * Cancelled} / {@code Error} / {@code Expired}). Drives the footer; pass
   * {@code null} for in-flight turns.
   */
  terminalStatus: TerminalRunStatus | null
  /**
   * Per-session run result (duration, model, artifacts, PR url). Optional —
   * the footer degrades gracefully when not yet emitted.
   */
  runResult?: RunResultDto | null
  /**
   * Fired when the user clicks the trailing cancel control on a live pill.
   * Wired through to {@code cancelTurn} by the parent. {@code undefined}
   * means "no cancel affordance".
   */
  onCancel?: () => void
  /**
   * Card 8 (cursor-native-chat-ux §7): the user has clicked the cancel
   * control and we're waiting on the daemon's terminal
   * {@code SDKStatusMessage(CANCELLED)} to land via SignalR. While this is
   * {@code true}, the pill state is forced to {@code cancelling} (label
   * "Cancelling…" + shimmer) regardless of what the underlying event stream
   * derives — gives the user immediate visual feedback that their click
   * registered. The real {@code cancelled} state takes over the moment the
   * status event arrives in the events list (or the session lands terminal
   * via the sessions list refetch).
   *
   * <p>If the derived state is already {@code cancelled} or {@code expired},
   * the override bows out — the wire-truth has caught up and we let the
   * resting terminal pill take over (so we don't stay stuck on
   * "Cancelling…" forever after the synthetic event lands, and an
   * {@code expired} ruling beats any user-cancel intent). We deliberately
   * do NOT bow out on {@code run-error}: if the user cancelled, the user
   * cancelled — a raced error event is noise. The backend's synthetic
   * Cancelled + sticky pill derivation should resolve to {@code cancelled}
   * before this code sees {@code run-error} in practice; the override
   * keeps the pill honest in the brief window where the optimistic flag
   * is set but the synthetic event hasn't landed yet AND a daemon Error
   * arrived first.</p>
   */
  optimisticCancelling?: boolean
}

// ── Thinking preview ───────────────────────────────────────────────────────

const PREVIEW_MAX_CHARS = 120

/**
 * Whether the LATEST event in this phase is a not-yet-complete
 * {@code ThinkingEvent}. We surface a quiet inline preview only while the
 * thinking is still the headline activity (no later tool / assistant text).
 */
function findActiveThinkingText(events: AgentEventDto[]): string | null {
  if (events.length === 0) return null
  const last = events[events.length - 1]
  if (!isThinking(last)) return null
  const text = (last.text ?? '').trim()
  if (!text) return null
  if (text.length <= PREVIEW_MAX_CHARS) return text
  // Collapse to a single line + ellipsis. We also strip leading/trailing
  // whitespace so the preview doesn't leak the source's indentation.
  return `${text.slice(0, PREVIEW_MAX_CHARS - 1).replace(/\s+/g, ' ').trim()}…`
}

// ── Find the "start ms" for the active live state, for elapsed counter ──

function activePillTimer(
  state: ActivityPillState,
  events: AgentEventDto[],
): { startMs: number | undefined; active: boolean } {
  // Only tool-running and thinking carry an elapsed counter.
  if (state.kind === 'tool-running') {
    // Walk back to find the latest still-running toolUse and use its
    // createdAt as the start. Falls back to "no timer" if unparseable.
    for (let i = events.length - 1; i >= 0; i--) {
      const evt = events[i]
      if (evt.eventKind !== 'toolUse') continue
      const startMs = Date.parse(evt.createdAt)
      return { startMs: Number.isFinite(startMs) ? startMs : undefined, active: true }
    }
  }
  if (state.kind === 'thinking') {
    for (let i = events.length - 1; i >= 0; i--) {
      const evt = events[i]
      if (evt.eventKind !== 'thinking') continue
      const startMs = Date.parse(evt.createdAt)
      return { startMs: Number.isFinite(startMs) ? startMs : undefined, active: true }
    }
  }
  return { startMs: undefined, active: false }
}

// ── Map AgentEventRunStatus → TerminalRunStatus (or null) ─────────────────
//
// Exported for the ChatCanvas caller so the lookup table lives in one place.
export function deriveTerminalStatusFromEvents(
  events: AgentEventDto[],
): TerminalRunStatus | null {
  // Walk backwards — the most recent terminal status wins.
  for (let i = events.length - 1; i >= 0; i--) {
    const evt = events[i]
    if (!isStatus(evt)) continue
    switch (evt.status) {
      case 'Finished':
        return 'Finished'
      case 'Cancelled':
        return 'Cancelled'
      case 'Error':
        return 'Error'
      case 'Expired':
        return 'Expired'
      default:
        // Creating / Running — not terminal; keep looking.
        break
    }
  }
  return null
}

// ── Component ───────────────────────────────────────────────────────────────

export function TurnPhaseChrome({
  sessionId,
  events,
  isLiveTurn,
  isLastPhase,
  terminalStatus,
  runResult,
  onCancel,
  optimisticCancelling,
}: TurnPhaseChromeProps) {
  // Convert events to Messages so deriveActivityPillState sees the typed
  // domain shape. This is the same projection {@code eventToMessage} uses
  // elsewhere — keeping a single source of truth.
  const messages = useMemo<Message[]>(() => {
    const out: Message[] = []
    for (const evt of events) {
      const m = eventToMessage(evt)
      if (m) out.push(m)
    }
    return out
  }, [events])

  // Derive the pill state. The elapsed counter is then overlaid via the
  // useElapsedMs hook so a tool-running state ticks live without re-running
  // the derivation on every interval frame.
  const baseState = useMemo(
    () => deriveActivityPillState(messages, isLiveTurn),
    [messages, isLiveTurn],
  )

  const { startMs, active } = activePillTimer(baseState, events)
  const liveElapsedMs = useElapsedMs(startMs, isLiveTurn && active)

  // Overlay the live counter onto the state. We only mutate the elapsed
  // field — the kind and the rest of the state are stable, so the pill's
  // crossfade hook won't fire on every tick.
  //
  // Card 8: when {@code optimisticCancelling} is set, we override the
  // derived state to {@code cancelling} so the pill reads "Cancelling…"
  // the instant the user clicks the cancel control — before the daemon's
  // terminal {@code Cancelled} status event arrives. We only bow out
  // when the wire shows a TERMINAL pill state — including {@code cancelled}
  // (the synthetic event landed, transition to the resting cancelled pill
  // instead of staying stuck on "Cancelling…" forever) and {@code expired}
  // (the turn timed out before cancel resolved). We deliberately do NOT
  // bow out on {@code run-error}: if the user cancelled, the user
  // cancelled — a raced error event is noise. The backend's synthetic
  // Cancelled + sticky pill derivation should resolve to {@code cancelled}
  // before this code sees {@code run-error} in practice, but this is the
  // belt-and-suspenders.
  const pillState = useMemo<ActivityPillState>(() => {
    if (
      optimisticCancelling &&
      baseState.kind !== 'cancelled' &&
      baseState.kind !== 'expired'
    ) {
      return { kind: 'cancelling' }
    }
    if (!isLiveTurn) return baseState
    if (baseState.kind === 'tool-running') {
      return { ...baseState, elapsedMs: liveElapsedMs }
    }
    if (baseState.kind === 'thinking') {
      return { ...baseState, elapsedMs: liveElapsedMs }
    }
    return baseState
  }, [baseState, liveElapsedMs, isLiveTurn, optimisticCancelling])

  // Inline thinking preview — only when the LATEST event is a still-active
  // thinking event. Once the thought completes (next event arrives) we
  // collapse it; the full text remains in the expanded trace.
  const thinkingPreview =
    pillState.kind === 'thinking' ? findActiveThinkingText(events) : null

  // Expanded trace state — kept local since each phase chrome owns its own
  // chevron. Older phases stay closed by default.
  const [expanded, setExpanded] = useState(false)

  // Cancel only fires while the pill is in a live state. The pill itself
  // gates the visible affordance via its showCancel rule — we pass through
  // onCancel verbatim and let the pill decide.
  const cancelHandler =
    onCancel && isLiveTurn && isLastPhase ? onCancel : undefined

  return (
    <Box
      data-session-id={sessionId}
      data-live-turn={isLiveTurn ? 'true' : 'false'}
      sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}
    >
      {/* Row 1: pill + chevron expand affordance */}
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 0.75,
        }}
      >
        <ActivityPill
          state={pillState}
          onCancel={cancelHandler}
          testIdPrefix={`phase-${sessionId}`}
        />
        <Box
          component="button"
          type="button"
          onClick={() => setExpanded((v) => !v)}
          aria-expanded={expanded}
          aria-label={expanded ? 'Hide trace' : 'Show trace'}
          data-testid={`phase-${sessionId}-trace-toggle`}
          sx={{
            background: 'none',
            border: 'none',
            padding: 0,
            cursor: 'pointer',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 22,
            height: 22,
            borderRadius: '50%',
            color: workspaceText.muted,
            opacity: 0.55,
            transition: 'opacity 180ms ease, background-color 180ms ease, color 180ms ease',
            '&:hover': {
              opacity: 1,
              color: workspaceText.primary,
              backgroundColor: surfaceTokens.chipHoverBg,
            },
            '&:focus-visible': {
              opacity: 1,
              outline: 'none',
              color: workspaceText.primary,
              backgroundColor: surfaceTokens.chipHoverBg,
            },
          }}
        >
          {expanded ? (
            <ExpandLessIcon sx={{ fontSize: 16 }} />
          ) : (
            <ExpandMoreIcon sx={{ fontSize: 16 }} />
          )}
        </Box>
      </Box>

      {/* Row 2: inline thinking preview — only while the thought is the
          headline activity. Dim italic, single line, ellipsis. */}
      {thinkingPreview && (
        <Typography
          component="div"
          data-testid={`phase-${sessionId}-thinking-preview`}
          sx={{
            ml: 1.5,
            fontSize: 12,
            fontStyle: 'italic',
            color: workspaceText.faint,
            lineHeight: 1.5,
            letterSpacing: '0.005em',
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            maxWidth: 640,
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
          }}
        >
          {thinkingPreview}
        </Typography>
      )}

      {/* Row 3: expanded trace */}
      {expanded && <TurnTrace events={events} />}

      {/* Row 4: turn footer — only on the LAST phase when terminal */}
      {isLastPhase && terminalStatus && (
        <TurnFooter
          status={terminalStatus}
          runResult={runResult ?? null}
          testIdPrefix={`phase-${sessionId}`}
        />
      )}
    </Box>
  )
}
