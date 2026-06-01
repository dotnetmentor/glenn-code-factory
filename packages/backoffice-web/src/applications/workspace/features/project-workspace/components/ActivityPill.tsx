/**
 * ActivityPill — the headline UX surface of the Cursor-native chat (card 6 of
 * cursor-native-chat-ux §3).
 *
 * <p>A slim horizontal capsule anchored to each assistant message. The latest
 * turn's pill is "live" — it animates and reflects the current tool. Older
 * turns' pills are frozen in their last state, so scrolling chat history
 * shows what each turn did at a glance.</p>
 *
 * <p>This component is a <b>pure render</b> of an {@link ActivityPillState}.
 * The caller (card 7's chat canvas) is responsible for computing the state
 * from the event stream — the pill knows nothing about events, sessions, or
 * runs. That keeps the state-machine spec (§3) testable in isolation and
 * lets the pill be reused for any future "live status" surface.</p>
 *
 * <h3>Visual rules (spec §3)</h3>
 * <ul>
 *   <li><b>Shimmer underline.</b> 1-2px gradient that translates left-to-right
 *       over ~1.5s while the pill is in any "live" state. NOT a spinner.
 *       NOT a bouncing dot. Fades on state change.</li>
 *   <li><b>Label transitions.</b> Crossfade on state change (~150ms opacity),
 *       never slide. The pill should feel like it's "settling" between
 *       states, not jumping.</li>
 *   <li><b>Elapsed counter.</b> The only thing that animates while a tool is
 *       running and nothing else changes. Updates at ~10fps — over-animation
 *       reads as nervous.</li>
 *   <li><b>No spinners. No bouncing. No pulsing dots.</b> Motion is in
 *       service of meaning.</li>
 * </ul>
 *
 * <h3>Card boundary</h3>
 * <p>This card builds the standalone component + tests. Wiring into
 * {@code ChatCanvas} is card 7. Implementing the real cancel API call is
 * card 8 — the pill only fires {@link ActivityPillProps.onCancel}. The
 * "finished" terminal state is handled by the caller passing the LAST
 * tool's frozen state (one of {@code tool-completed} / {@code tool-error});
 * the pill itself has no dedicated {@code finished} branch.</p>
 */
import { Box, Typography } from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import PsychologyIcon from '@mui/icons-material/Psychology'
import AssignmentIcon from '@mui/icons-material/Assignment'
import BuildIcon from '@mui/icons-material/Build'
import TerminalIcon from '@mui/icons-material/Terminal'
import DescriptionIcon from '@mui/icons-material/Description'
import EditIcon from '@mui/icons-material/Edit'
import SearchIcon from '@mui/icons-material/Search'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import NoteAddIcon from '@mui/icons-material/NoteAdd'
import PublicIcon from '@mui/icons-material/Public'
import { keyframes } from '@mui/system'
import { useEffect, useRef, useState, type ComponentType, type SVGProps } from 'react'

import type { FormatterOutput } from './toolFormatters'
import { semanticTokens, surfaceTokens, workspaceText } from '../../../shared/designTokens'

// ── Public state shape ──────────────────────────────────────────────────────

/**
 * Discriminated union of every pill state from spec §3. The caller computes
 * this from its event stream and hands it to the pill verbatim — the
 * component does no derivation of its own.
 *
 * <p>Notable choices:
 * <ul>
 *   <li>{@code tool-running} / {@code thinking} carry {@code elapsedMs}
 *       (caller-tracked) — the pill formats it but does not own the timer.
 *       When the caller passes an updated elapsed value the pill re-renders
 *       the trailing counter only; the label crossfade is skipped because
 *       the state {@code kind} hasn't changed.</li>
 *   <li>{@code tool-completed} / {@code tool-error} carry the full
 *       {@link FormatterOutput} so the pill can show the formatter's
 *       summary / errorVariant + glyph without re-running the registry.</li>
 *   <li>There is no {@code finished} branch. The caller maps a
 *       {@code RunStatus.FINISHED} turn to the LAST tool's frozen state
 *       (typically {@code tool-completed}) so scrolling history reads as
 *       "this turn ended with this tool". See spec §3 final paragraph.</li>
 * </ul></p>
 */
export type ActivityPillState =
  | { kind: 'creating' }
  | { kind: 'running-idle'; taskTitle?: string }
  | { kind: 'tool-running'; formatted: FormatterOutput; elapsedMs: number }
  | { kind: 'thinking'; elapsedMs: number }
  /**
   * Frozen counterpart to {@code thinking} — used for closed phases / older
   * turns where the model has finished thinking. Past-tense label, no shimmer,
   * success chip — visually communicates "this thought is done" instead of
   * the nervous live-thinking shimmer that previously persisted forever.
   */
  | { kind: 'thinking-done'; elapsedMs: number }
  | { kind: 'tool-completed'; formatted: FormatterOutput }
  | { kind: 'tool-error'; formatted: FormatterOutput }
  | { kind: 'cancelling' }
  | { kind: 'cancelled' }
  | { kind: 'run-error' }
  | { kind: 'expired' }

export interface ActivityPillProps {
  /**
   * Caller-computed state from the turn's events. The pill renders this
   * verbatim — see {@link ActivityPillState} for the discriminated union.
   */
  state: ActivityPillState
  /**
   * Fired when the user clicks the trailing cancel control. Only rendered
   * while the pill is in a live state ({@code creating} / {@code running-idle}
   * / {@code tool-running} / {@code thinking} / {@code cancelling}). Card 8
   * wires the real cancellation API call — for now, callers pass a noop
   * or a stub.
   */
  onCancel?: () => void
  /**
   * Optional prefix for every {@code data-testid} the pill emits. Lets
   * Playwright (card 9) disambiguate multiple pills on the same page,
   * e.g. {@code testIdPrefix="turn-3"} → {@code turn-3-pill-root}.
   */
  testIdPrefix?: string
}

// ── Tokens ──────────────────────────────────────────────────────────────────

const PILL_HEIGHT = 26
const PILL_RADIUS = 999
const LABEL_FADE_MS = 150
const SHIMMER_DURATION_MS = 1500
const ELAPSED_TICK_MS = 100 // 10fps — spec §3 "Updates at ~10fps"
const HOVER_TRANSITION_MS = 180

// Status chip colors. Spec §3:
//   green dot   = tool-completed
//   amber dot   = tool-error
//   red dot     = run-error
//   neutral dot = cancelled / expired
const STATUS_COLORS = {
  success: semanticTokens.success,
  warning: semanticTokens.warning,
  error: semanticTokens.error,
  neutral: workspaceText.faint,
} as const

// ── Shimmer keyframes ───────────────────────────────────────────────────────
//
// A 1px gradient bar that slides left → right along the bottom edge. We do
// this with a translateX on a child Box rather than a `background-position`
// animation so the gradient itself can blur to transparent at both ends
// (otherwise the bar pops on/off-screen rather than fading in/out).
const shimmerSlide = keyframes`
  0%   { transform: translateX(-100%); }
  100% { transform: translateX(100%); }
`

// ── Icon lookup ─────────────────────────────────────────────────────────────
//
// Formatters declare their glyph by MUI icon name (string). We map it to a
// component at render time. Unknown names fall through to {@code undefined}
// so the leading slot collapses gracefully — never a "missing icon" box.
//
// Keep this map small: only the icons the formatter registry actually uses
// (card 5) plus the two non-tool slots (thinking, task). Adding a new tool
// formatter means one entry here when its glyph isn't already covered.
type IconComponent = ComponentType<SVGProps<SVGSVGElement> & { fontSize?: string }>

const GLYPH_MAP: Readonly<Record<string, IconComponent>> = {
  Terminal: TerminalIcon as unknown as IconComponent,
  Description: DescriptionIcon as unknown as IconComponent,
  Edit: EditIcon as unknown as IconComponent,
  Search: SearchIcon as unknown as IconComponent,
  FolderOpen: FolderOpenIcon as unknown as IconComponent,
  NoteAdd: NoteAddIcon as unknown as IconComponent,
  Public: PublicIcon as unknown as IconComponent,
  Build: BuildIcon as unknown as IconComponent,
  // Non-tool glyphs — used directly by this component for `thinking` /
  // `running-idle (with task)`.
  Psychology: PsychologyIcon as unknown as IconComponent,
  Assignment: AssignmentIcon as unknown as IconComponent,
}

function resolveGlyph(name: string | undefined): IconComponent | undefined {
  if (!name) return undefined
  return GLYPH_MAP[name]
}

// ── Elapsed counter ─────────────────────────────────────────────────────────
//
// Formats ms as the most-compact readable form:
//   < 1s     → "0.4s"
//   < 60s    → "12s"
//   < 1h     → "1m 14s"
//   >= 1h    → "1h 02m"
//
// Tabular-num + a monospace fallback so the counter doesn't jitter as digits
// change width. The pill width is otherwise stable because the only varying
// glyph is the counter itself.
export function formatElapsed(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '0.0s'
  // Sub-10s: tenths (matches spec scene 2 "0.4s → 1.2s"). Above 10s the
  // tenths digit reads as nervous — drop to whole seconds.
  if (ms < 10_000) {
    return `${(ms / 1000).toFixed(1)}s`
  }
  const totalSeconds = Math.floor(ms / 1000)
  if (totalSeconds < 60) {
    return `${totalSeconds}s`
  }
  const totalMinutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  if (totalMinutes < 60) {
    return `${totalMinutes}m ${seconds.toString().padStart(2, '0')}s`
  }
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours}h ${minutes.toString().padStart(2, '0')}m`
}

// ── State → presentation helpers ────────────────────────────────────────────

interface Presentation {
  /** Label text — empty string means "no label slot". */
  label: string
  /** MUI icon component for the leading slot, or undefined for no glyph. */
  Glyph: IconComponent | undefined
  /** Whether the shimmer underline animates. */
  shimmer: boolean
  /** Trailing elapsed counter — undefined means no counter rendered. */
  elapsedMs: number | undefined
  /** Status dot color — undefined means no status chip rendered. */
  chip: keyof typeof STATUS_COLORS | undefined
  /** Whether the cancel control should render (visible on hover/focus). */
  showCancel: boolean
  /** Tone — drives the label color. */
  tone: 'normal' | 'muted' | 'error'
}

function presentationFor(state: ActivityPillState): Presentation {
  switch (state.kind) {
    case 'creating':
      return {
        label: 'Starting…',
        Glyph: undefined,
        shimmer: true,
        elapsedMs: undefined,
        chip: undefined,
        showCancel: true,
        tone: 'muted',
      }
    case 'running-idle':
      return {
        label: state.taskTitle ?? 'Working…',
        Glyph: state.taskTitle ? GLYPH_MAP.Assignment : undefined,
        shimmer: true,
        elapsedMs: undefined,
        chip: undefined,
        showCancel: true,
        tone: state.taskTitle ? 'normal' : 'muted',
      }
    case 'tool-running':
      return {
        label: state.formatted.activeLabel,
        Glyph: resolveGlyph(state.formatted.glyph),
        shimmer: true,
        elapsedMs: state.elapsedMs,
        chip: undefined,
        showCancel: true,
        tone: 'normal',
      }
    case 'thinking':
      return {
        label: 'Thinking',
        Glyph: GLYPH_MAP.Psychology,
        shimmer: true,
        elapsedMs: state.elapsedMs,
        chip: undefined,
        showCancel: true,
        tone: 'muted',
      }
    case 'thinking-done':
      return {
        label: 'Thought',
        Glyph: GLYPH_MAP.Psychology,
        shimmer: false,
        // Only surface a duration when we actually have one — otherwise the
        // trailing slot collapses (no "0.0s" reading nervous on history).
        elapsedMs: state.elapsedMs > 0 ? state.elapsedMs : undefined,
        chip: 'success',
        showCancel: false,
        tone: 'muted',
      }
    case 'tool-completed':
      return {
        label: state.formatted.summary,
        Glyph: resolveGlyph(state.formatted.glyph),
        shimmer: false,
        elapsedMs: undefined,
        chip: 'success',
        showCancel: false,
        tone: 'normal',
      }
    case 'tool-error':
      return {
        label: state.formatted.errorVariant,
        Glyph: resolveGlyph(state.formatted.glyph),
        shimmer: false,
        elapsedMs: undefined,
        chip: 'warning',
        showCancel: false,
        tone: 'error',
      }
    case 'cancelling':
      return {
        label: 'Cancelling…',
        Glyph: undefined,
        shimmer: true,
        elapsedMs: undefined,
        chip: undefined,
        showCancel: true,
        tone: 'muted',
      }
    case 'cancelled':
      return {
        label: 'Cancelled',
        Glyph: undefined,
        shimmer: false,
        elapsedMs: undefined,
        chip: 'neutral',
        showCancel: false,
        tone: 'muted',
      }
    case 'run-error':
      return {
        label: 'Stopped',
        Glyph: undefined,
        shimmer: false,
        elapsedMs: undefined,
        chip: 'error',
        showCancel: false,
        tone: 'error',
      }
    case 'expired':
      return {
        label: 'Timed out',
        Glyph: undefined,
        shimmer: false,
        elapsedMs: undefined,
        chip: 'neutral',
        showCancel: false,
        tone: 'muted',
      }
  }
}

// ── Crossfade hook ──────────────────────────────────────────────────────────
//
// Keeps the rendered label/glyph stable for {@link LABEL_FADE_MS} after a
// state change so we can opacity-fade between the two. We track:
//   * the displayed presentation (what's on screen right now)
//   * a "fading" flag that flips to true at the moment of the state change
//     and back to false once the fade has had a chance to settle.
//
// We DON'T cross-fade on elapsed-only updates inside the same {@code kind}.
// The state-machine label is keyed on {@code state.kind} plus the formatter's
// labels (for tool states) — so a 100ms elapsed tick doesn't trigger a fade.
function useCrossfadedPresentation(state: ActivityPillState): {
  presentation: Presentation
  fading: boolean
} {
  const presentation = presentationFor(state)

  // The fade trigger key: anything that changes the visible label or glyph
  // (but NOT the elapsed counter, which animates inside the trailing slot).
  const fadeKey = `${state.kind}|${presentation.label}|${presentation.Glyph ? 1 : 0}`

  const [fading, setFading] = useState(false)
  const lastKeyRef = useRef(fadeKey)

  useEffect(() => {
    if (lastKeyRef.current !== fadeKey) {
      lastKeyRef.current = fadeKey
      setFading(true)
      const timeout = window.setTimeout(() => setFading(false), LABEL_FADE_MS)
      return () => window.clearTimeout(timeout)
    }
    return undefined
  }, [fadeKey])

  return { presentation, fading }
}

// ── Component ───────────────────────────────────────────────────────────────

export function ActivityPill({
  state,
  onCancel,
  testIdPrefix,
}: ActivityPillProps) {
  const { presentation, fading } = useCrossfadedPresentation(state)
  const { label, Glyph, shimmer, elapsedMs, chip, showCancel, tone } =
    presentation

  // Prefix every test id so multiple pills on a page (Storybook, e2e fixtures
  // with frozen historical turns + a live current turn) don't collide.
  const tid = (suffix: string) =>
    testIdPrefix ? `${testIdPrefix}-${suffix}` : suffix

  const labelColor =
    tone === 'error'
      ? semanticTokens.error
      : tone === 'muted'
        ? workspaceText.muted
        : workspaceText.primary

  return (
    <Box
      data-testid={tid('pill-root')}
      data-state={state.kind}
      role="status"
      aria-live="polite"
      sx={{
        // Slim capsule. Height ≈ single line of body text. Tight padding.
        display: 'inline-flex',
        alignItems: 'center',
        gap: 0.75,
        height: PILL_HEIGHT,
        px: 1.25,
        borderRadius: `${PILL_RADIUS}px`,
        backgroundColor: surfaceTokens.chipBg,
        border: `1px solid ${surfaceTokens.hairline}`,
        position: 'relative',
        overflow: 'hidden',
        maxWidth: '100%',
        boxSizing: 'border-box',
        // Hover reveals the cancel control. Hover/focus also brightens the
        // background subtly so the affordance is discoverable.
        transition: `background-color ${HOVER_TRANSITION_MS}ms ease`,
        '&:hover, &:focus-within': {
          backgroundColor: surfaceTokens.chipHoverBg,
          '& .pill-cancel': {
            opacity: 1,
            pointerEvents: 'auto',
          },
        },
      }}
    >
      {/* Leading glyph — fades with the label so a state change reads as one
          composed motion, not two staggered animations. */}
      {Glyph && (
        <Box
          aria-hidden
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 14,
            height: 14,
            flexShrink: 0,
            color: labelColor,
            opacity: fading ? 0 : 0.85,
            transition: `opacity ${LABEL_FADE_MS}ms ease`,
          }}
        >
          <Glyph style={{ fontSize: 14, width: 14, height: 14 }} />
        </Box>
      )}

      {/* Label — body weight, single line, ellipsis on overflow. Crossfades
          on state changes. {@code data-fading} is a stable test hook for the
          crossfade behaviour — sx-based opacity emits dynamic classes that
          jsdom/happy-dom can't reliably read back. */}
      <Typography
        data-testid={tid('pill-label')}
        data-fading={fading ? 'true' : 'false'}
        component="span"
        sx={{
          flex: '1 1 auto',
          minWidth: 0,
          fontSize: 12.5,
          fontWeight: 500,
          lineHeight: 1.2,
          letterSpacing: '-0.005em',
          color: labelColor,
          whiteSpace: 'nowrap',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          opacity: fading ? 0 : 1,
          transition: `opacity ${LABEL_FADE_MS}ms ease, color ${HOVER_TRANSITION_MS}ms ease`,
        }}
      >
        {label}
      </Typography>

      {/* Elapsed counter — monospace tabular so digits don't jitter. Only
          rendered for the two states that carry an elapsedMs (tool-running,
          thinking). Updates at the caller's cadence (10fps via useElapsedMs
          below, when the caller opts in). */}
      {elapsedMs !== undefined && (
        <Box
          data-testid={tid('pill-elapsed')}
          component="span"
          sx={{
            flexShrink: 0,
            fontSize: 11,
            fontFamily: 'ui-monospace, SFMono-Regular, "SF Mono", Menlo, monospace',
            fontVariantNumeric: 'tabular-nums',
            color: workspaceText.faint,
            lineHeight: 1.2,
          }}
        >
          {formatElapsed(elapsedMs)}
        </Box>
      )}

      {/* Status chip — small dot. Spec calls for "small dot, not a chip with
          text" so no label, no background, just a 6px circle. */}
      {chip && (
        <Box
          aria-hidden
          data-testid={tid('pill-status-chip')}
          data-chip-variant={chip}
          sx={{
            flexShrink: 0,
            width: 6,
            height: 6,
            borderRadius: '50%',
            backgroundColor: STATUS_COLORS[chip],
            transition: `background-color ${HOVER_TRANSITION_MS}ms ease`,
          }}
        />
      )}

      {/* Cancel control — single ✕ glyph, hidden by default, revealed on
          hover/focus-within of the pill. Only rendered for live states so
          the DOM doesn't carry a permanent disabled button. */}
      {showCancel && onCancel && (
        <Box
          component="button"
          type="button"
          data-testid={tid('pill-cancel')}
          className="pill-cancel"
          onClick={(event: React.MouseEvent) => {
            event.stopPropagation()
            onCancel()
          }}
          aria-label="Cancel"
          sx={{
            flexShrink: 0,
            // Reset native button chrome.
            background: 'none',
            border: 'none',
            padding: 0,
            margin: 0,
            cursor: 'pointer',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 16,
            height: 16,
            borderRadius: '50%',
            color: workspaceText.muted,
            opacity: 0,
            pointerEvents: 'none',
            transition: `opacity ${HOVER_TRANSITION_MS}ms ease, color ${HOVER_TRANSITION_MS}ms ease, background-color ${HOVER_TRANSITION_MS}ms ease`,
            '&:hover, &:focus-visible': {
              opacity: 1,
              pointerEvents: 'auto',
              color: workspaceText.primary,
              backgroundColor: surfaceTokens.chipHoverBg,
              outline: 'none',
            },
          }}
        >
          <CloseIcon style={{ fontSize: 12, width: 12, height: 12 }} />
        </Box>
      )}

      {/* Shimmer underline — subtle 1px gradient bar that translates
          left→right while the pill is live. Fades in/out via opacity so the
          shimmer doesn't pop on state change. */}
      <Box
        aria-hidden
        data-testid={tid('pill-shimmer')}
        data-shimmer-active={shimmer ? 'true' : 'false'}
        sx={{
          position: 'absolute',
          bottom: 0,
          left: 0,
          right: 0,
          height: 1,
          overflow: 'hidden',
          opacity: shimmer ? 1 : 0,
          transition: `opacity ${LABEL_FADE_MS}ms ease`,
          pointerEvents: 'none',
        }}
      >
        <Box
          sx={{
            width: '100%',
            height: '100%',
            // The gradient itself fades at both ends so the bar reads as a
            // travelling highlight rather than a hard edge crossing the
            // pill. Color is the accent ink at ~40% so it never competes
            // with the label.
            background: `linear-gradient(90deg, transparent 0%, ${workspaceText.muted} 50%, transparent 100%)`,
            opacity: 0.6,
            animation: shimmer
              ? `${shimmerSlide} ${SHIMMER_DURATION_MS}ms linear infinite`
              : 'none',
          }}
        />
      </Box>
    </Box>
  )
}

// ── Utility hook for callers (card 7) ───────────────────────────────────────
//
// Returns a continuously-updating elapsedMs value for callers that want a
// live counter but don't want to manage their own setInterval. Spec-aligned
// 10fps cadence — over-animation reads as nervous. Pass the wall-clock
// start time; the hook ticks every 100ms while {@code active === true} and
// stops cleanly when toggled off.
//
// Exported alongside the component (not from a separate file) so consumers
// only need one import.
export function useElapsedMs(
  startMs: number | undefined,
  active: boolean,
): number {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    // Defense-in-depth: the interval is ONLY created while the turn is
    // genuinely active. The cleanup below clears it the instant {@code active}
    // toggles false (or the component unmounts), so a finished / idle turn can
    // never keep a 100ms ticker alive — the perpetual-commit bug this hook was
    // implicated in is gated entirely by the {@code active} flag the caller
    // passes (caller ANDs it with the live-turn gate). If there's no start
    // time there is nothing to tick, so we also bail in that case.
    if (!active || startMs === undefined) return undefined
    // Re-sync immediately so the first tick reflects "now" rather than a stale
    // value captured the last time the hook was active.
    setNow(Date.now())
    const interval = window.setInterval(() => setNow(Date.now()), ELAPSED_TICK_MS)
    return () => window.clearInterval(interval)
  }, [active, startMs])

  if (startMs === undefined) return 0
  return Math.max(0, now - startMs)
}
