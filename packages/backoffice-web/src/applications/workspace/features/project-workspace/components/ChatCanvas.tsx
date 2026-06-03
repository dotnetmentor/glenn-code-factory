import {
  memo,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  CircularProgress,
  Collapse,
  Fab,
  IconButton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { keyframes } from '@mui/system'
import { HubConnectionState } from '@microsoft/signalr'
import SendIcon from '@mui/icons-material/ArrowUpward'
import ContentCopyIcon from '@mui/icons-material/ContentCopyOutlined'
import CheckIcon from '@mui/icons-material/CheckOutlined'
import UndoIcon from '@mui/icons-material/UndoOutlined'
import StopIcon from '@mui/icons-material/StopOutlined'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import AttachFileIcon from '@mui/icons-material/AttachFileOutlined'
import { useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import { format, parseISO } from 'date-fns'
import {
  AgentSessionStatus,
  RuntimeState,
  RuntimeProposalStatus,
  getApiSessionsIdEvents,
  getGetApiConversationsIdQueryKey,
  getGetApiProjectsProjectIdConversationsQueryKey,
  useGetApiCursorModelsActive,
  useGetApiConversationsId,
  useGetApiProjectsProjectId,
  useGetApiProjectsProjectIdBranches,
  useGetApiProjectsProjectIdProposals,
  useGetApiSessionsIdRunResult,
  type AgentEventDto,
  type CursorModelDto,
  type RuntimeProposalDto,
  type SessionSummary,
} from '../../../../../api/queries-commands'
import type { AgentHubConnection } from '../../../../../lib/signalr'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { eventToMessage, isStatus, type Message } from './chatEvents'
import { AssistantMarkdown } from './AssistantMarkdown'
import {
  TurnPhaseChrome,
  deriveTerminalStatusFromEvents,
} from './TurnPhaseChrome'
import type { TerminalRunStatus } from './TurnFooter'
import { readAgentModelOverride } from '../hooks/useAgentModelOverride'
import { clearLastBranchConversationId } from '../hooks/branchConversationMemory'
import {
  RuntimeProposalCard,
  useProposalSignalR,
} from '../../../../super-admin/features/project-runtime'
import { ComposerModelPickerInline } from './ComposerModelPickerInline'
import { ComposerYoloToggle } from './ComposerYoloToggle'
import { SessionCostBadge } from './SessionCostBadge'
import { useStickToBottom } from './useStickToBottom'
import { PastAttachmentChip } from './PastAttachmentChip'
import {
  PendingAttachmentSlot,
  type SlotStateSnapshot,
} from './PendingAttachmentSlot'

import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens, ...semanticTokens }

// ── Design tokens ──────────────────────────────────────────────────────────

const fadeIn = keyframes`
  from { opacity: 0; }
  to   { opacity: 1; }
`

// ── Component props ────────────────────────────────────────────────────────

export interface ChatCanvasProps {
  projectId: string
  branchId: string
  /**
   * The currently-active conversation. {@code null} means the user is in the
   * empty-canvas state — no transcript, just the empty composition centered
   * on the canvas. Submitting from there creates a new conversation and the
   * parent route updates {@code ?c=} to its id.
   */
  conversationId: string | null
  /** Live AgentHub connection lifted from the route. May be {@code null} while connecting. */
  connection: AgentHubConnection | null
  /**
   * Effective runtime state lifted from the route. Wired in P4.2 so the
   * composer can show a wake-aware placeholder when the runtime is Suspended
   * or Suspending. P4.2 will not visibly show this placeholder yet (the page
   * still routes Suspended to a legacy Paper view); P4.3 will mount this
   * canvas in the Suspended state and the prop "just works" from there.
   */
  runtimeState?: RuntimeState | string | null
  /**
   * Optional focus-state notifier for the composer textarea. Mobile callers
   * use this to collapse the bottom tab bar while the user is typing so the
   * on-screen keyboard has more vertical room. Desktop callers omit it.
   *
   * <p>The signal is debounced (~150ms) on blur so quick focus hand-offs to
   * adjacent composer affordances (e.g. tapping the Send button) don't briefly
   * flash the tab bar back into view.</p>
   */
  onComposerFocusChange?: (focused: boolean) => void
}

// ── Sub-component: a single chat bubble ───────────────────────────────────
//
// P3.2 collapsed all per-event status placeholders into one {@link TurnStatusLine}
// per turn (rendered separately in the transcript loop below). The bubble
// component therefore only handles user / assistant kinds; status messages
// are filtered out at the {@code messages} memo level.

interface MessageBubbleProps {
  /**
   * Optional friendly label for the agent model that handled this turn —
   * rendered as a quiet pill in the top-left corner of user bubbles. Pass
   * {@code null} (or omit) to suppress the pill, e.g. when the session model
   * isn't known yet, was deactivated, or the backend doesn't expose it.
   *
   * <p>The {@code Slug} is shown in a tooltip on hover so the user can verify
   * which Anthropic id ran without staring at the bubble for ten seconds.</p>
   */
  modelLabel?: string | null
  modelSlug?: string | null
  // The bubble only renders text-bearing kinds — `status` is folded into
  // TurnStatusLine elsewhere, and the P3.1 `commit` trailer has its own
  // dedicated component (see {@link CommitTrailerLine}).
  message: Extract<Message, { kind: 'user' } | { kind: 'assistant' }>
  /**
   * True when this user bubble represents a message whose session is
   * soft-queued behind another in-flight turn (status Pending). The bubble
   * renders a quieter "Queued" chip and surfaces the {@code onRevoke}
   * affordance below. Has no effect for assistant bubbles.
   */
  isQueued?: boolean
  /**
   * Revoke handler — only wired for queued user bubbles. Pulls the message
   * text back into the composer and cancels the pending session in one
   * gesture. Invoked with the bubble's text so the parent can populate the
   * composer without needing to thread the message through.
   */
  onRevoke?: (text: string) => void
  /**
   * Per-turn cost + token breakdown for this session, threaded through so the
   * user bubble can render a tiny whisper-style cost annotation in its
   * bottom-right corner. Only meaningful on the user prompt bubble of a
   * persisted session; passing {@code null} (or omitting) suppresses the
   * annotation entirely. Tucking the cost inside the user bubble — rather
   * than as a freestanding row below the assistant prose — keeps the
   * transcript visually quieter while still surfacing the value on a glance.
   */
  sessionCost?: {
    costUsd: number | null | undefined
    inputTokens: number | null | undefined
    outputTokens: number | null | undefined
    cacheReadTokens: number | null | undefined
    cacheWriteTokens: number | null | undefined
    reasoningTokens: number | null | undefined
  } | null
}

function MessageBubbleImpl({
  message,
  isQueued,
  onRevoke,
  modelLabel,
  modelSlug,
  sessionCost,
}: MessageBubbleProps) {
  const isUser = message.kind === 'user'
  // Only surface the model pill on persisted user bubbles. Assistant prose
  // has its own per-turn affordances elsewhere, and optimistic bubbles
  // haven't been associated with a real session yet — slapping a label on
  // them would be both jumpy and lying about what's about to happen.
  const showModelPill =
    isUser && !!modelLabel && !(message.kind === 'user' && message.optimistic)

  // ── Copy-to-clipboard state ────────────────────────────────────────────
  // The icon flips to a checkmark for ~1.2s after a successful copy as
  // tactile confirmation. We don't surface failures as an error toast — a
  // copy failure is harmless and the absence of the checkmark is enough
  // signal that something went wrong (cf. modern editors).
  const [copied, setCopied] = useState(false)
  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(message.text)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1200)
    } catch {
      // Clipboard API rejected (no permission, insecure context, …) —
      // swallow. Cmd+C on the text still works as a fallback.
    }
  }, [message.text])

  // Revoke is only meaningful for queued user bubbles. We hoist the binding
  // here so the action-row JSX stays flat.
  const canRevoke = isUser && isQueued === true && !!onRevoke
  const handleRevoke = useCallback(() => {
    onRevoke?.(message.text)
  }, [onRevoke, message.text])

  return (
    <Box
      data-message-id={message.id}
      data-message-kind={message.kind}
      sx={{
        display: 'flex',
        justifyContent: isUser ? 'flex-end' : 'flex-start',
        animation: `${fadeIn} 200ms ease`,
        // Cushion under the top edge of the scroll viewport for {@link
        // ChatCanvas} → "scroll user bubble to top on send" — gives the
        // bubble breathing room above it after smooth-scrollIntoView lands.
        scrollMarginTop: '24px',
        // Hover scope for the action row — the icons fade in only while the
        // pointer is over the bubble. Group hover via a parent class so the
        // child sx blocks can react with a sibling selector.
        '&:hover .message-actions': {
          opacity: 1,
        },
        // Same hover scope drives the assistant-only timestamp affordance —
        // a small {@code HH:mm} stamp that reveals on hover so the time the
        // turn finished is one glance away without cluttering the calm
        // transcript reading state.
        '&:hover .message-time': {
          opacity: 1,
        },
      }}
    >
      <Box
        sx={{
          position: 'relative',
          maxWidth: isUser ? '78%' : '100%',
          width: isUser ? 'auto' : '100%',
          px: isUser ? 1.75 : 0,
          py: isUser ? 1.25 : 0.25,
          ...(isUser &&
          sessionCost &&
          sessionCost.costUsd != null &&
          sessionCost.costUsd > 0
            ? { pb: 2 }
            : {}),
          borderRadius: isUser ? '14px' : 0,
          backgroundColor: isUser ? tokens.bubbleUser : 'transparent',
          border: 'none',
        }}
      >
        {/* Model pill — names the Anthropic model that handled this turn.
            Rendered as a quiet chip above the prompt text on user bubbles
            ONLY when we know which model ran (we never lie). Sits inline
            with the Queued chip when both are present so the row reads as
            one inline meta strip. */}
        {showModelPill && (
          <Tooltip title={modelSlug ?? ''} placement="top" enterDelay={400}>
            <Box
              component="span"
              sx={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 0.5,
                mb: 0.75,
                mr: isQueued ? 0.75 : 0,
                px: 0.75,
                py: 0.125,
                borderRadius: '999px',
                backgroundColor: 'rgba(0, 0, 0, 0.06)',
                color: tokens.textMuted,
                fontSize: '0.6875rem',
                fontWeight: 500,
                letterSpacing: '0.01em',
              }}
            >
              {modelLabel}
            </Box>
          </Tooltip>
        )}

        {/* Queued chip — only on user bubbles whose session is Pending. Sits
            inline above the prompt so the soft-queue rhythm is immediately
            legible without competing with the bubble content. */}
        {isUser && isQueued && (
          <Box
            sx={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 0.5,
              mb: 0.75,
              px: 0.75,
              py: 0.125,
              borderRadius: '999px',
              backgroundColor: tokens.accentSurface,
              color: tokens.accent,
              fontSize: '0.6875rem',
              fontWeight: 500,
              letterSpacing: '0.01em',
              textTransform: 'uppercase',
            }}
          >
            <Box
              sx={{
                width: 5,
                height: 5,
                borderRadius: '50%',
                backgroundColor: tokens.accent,
                opacity: 0.7,
              }}
            />
            Queued
          </Box>
        )}

        {isUser ? (
          <Typography
            component="div"
            sx={{
              fontSize: '0.9375rem',
              lineHeight: 1.55,
              letterSpacing: '-0.005em',
              color: tokens.textPrimary,
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              fontFamily:
                '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
            }}
          >
            {message.text}
          </Typography>
        ) : (
          // Assistant prose renders as real markdown — bold/italic, headings,
          // lists, inline + fenced code (with syntax highlighting), tables,
          // blockquotes, links. User messages keep the plain pre-wrap text
          // path above because users type prompts, not markdown.
          <AssistantMarkdown text={message.text} />
        )}

        {/* Hover-only timestamp. The transcript stays calm at rest — the
            time only appears under the pointer. Mirrored alignment so the
            stamp lives on the opposite corner from each bubble's load-
            bearing affordance: assistant bubbles place it bottom-right
            (away from the left edge where prose starts); user bubbles
            place it bottom-left so it never collides with the absolutely-
            positioned {@link SessionCostBadge} that occupies the bottom-
            right of cost-bearing user bubbles. Optimistic user bubbles
            have no real {@code createdAt} yet — the {@code &&} guard
            keeps them silent until the persisted {@code PromptReceived}
            row arrives and we know the actual time. We render the
            compact {@code HH:mm} on-screen and surface the full date+time
            in the native tooltip on hover for users who want second-
            precision. */}
        {message.createdAt && (
          <Tooltip
            title={(() => {
              try {
                return format(parseISO(message.createdAt), 'PPpp')
              } catch {
                return message.createdAt
              }
            })()}
            placement="top"
            enterDelay={400}
          >
            <Typography
              className="message-time"
              component="span"
              sx={{
                display: 'block',
                mt: 0.5,
                textAlign: isUser ? 'left' : 'right',
                fontSize: '0.6875rem',
                color: tokens.textFaint,
                fontVariantNumeric: 'tabular-nums',
                letterSpacing: '0.01em',
                opacity: 0,
                transition: 'opacity 150ms ease',
                // No interaction needed — purely informational.
                userSelect: 'none',
                pointerEvents: 'none',
              }}
            >
              {(() => {
                try {
                  return format(parseISO(message.createdAt), 'HH:mm')
                } catch {
                  return ''
                }
              })()}
            </Typography>
          </Tooltip>
        )}

        {/* Hover-reveal action row. Lives inside the bubble (so it inherits
            the bubble's hover state) and absolutely positions itself at the
            top-right corner. Icons are unobtrusive by default and fade in
            via the parent's {@code .message-actions} hover selector. */}
        <Box
          className="message-actions"
          sx={{
            position: 'absolute',
            top: -10,
            right: 8,
            display: 'flex',
            gap: 0.25,
            opacity: 0,
            transition: 'opacity 150ms ease',
            // The revoke button is only present on queued bubbles. We keep
            // the action row visible there even without hover so the user
            // can find revoke without having to discover it.
            ...(canRevoke
              ? {
                  opacity: 1,
                }
              : {}),
          }}
        >
          {canRevoke && (
            <Tooltip title="Revoke (return to composer)" placement="top">
              <IconButton
                size="small"
                onClick={handleRevoke}
                aria-label="Revoke queued message"
                sx={{
                  width: 24,
                  height: 24,
                  padding: 0,
                  backgroundColor: 'instrument.surface',
                  border: `1px solid ${tokens.hairline}`,
                  color: tokens.textMuted,
                  '&:hover': {
                    color: tokens.accent,
                    borderColor: tokens.accentBorderStrong,
                    backgroundColor: 'instrument.surface',
                  },
                }}
              >
                <UndoIcon sx={{ fontSize: 13 }} />
              </IconButton>
            </Tooltip>
          )}
          <Tooltip title={copied ? 'Copied' : 'Copy'} placement="top">
            <IconButton
              size="small"
              onClick={handleCopy}
              aria-label="Copy message"
              sx={{
                width: 24,
                height: 24,
                padding: 0,
                backgroundColor: 'instrument.surface',
                border: `1px solid ${tokens.hairline}`,
                color: copied ? tokens.accent : tokens.textMuted,
                '&:hover': {
                  color: tokens.textPrimary,
                  backgroundColor: 'instrument.surface',
                },
              }}
            >
              {copied ? (
                <CheckIcon sx={{ fontSize: 13 }} />
              ) : (
                <ContentCopyIcon sx={{ fontSize: 13 }} />
              )}
            </IconButton>
          </Tooltip>
        </Box>

        {/* Per-turn cost whisper — tucked into the bottom-right corner of the
            user bubble in compact mode. Hides itself when the cost is null
            (legacy / canceled turns) or exactly zero ($0 inside a bubble is
            visual noise), so vanilla short turns don't sprout a tag. Hover
            still surfaces the full token breakdown via the SessionCostBadge
            tooltip. */}
        {isUser && sessionCost && (
          <SessionCostBadge
            costUsd={sessionCost.costUsd}
            inputTokens={sessionCost.inputTokens}
            outputTokens={sessionCost.outputTokens}
            cacheReadTokens={sessionCost.cacheReadTokens}
            cacheWriteTokens={sessionCost.cacheWriteTokens}
            reasoningTokens={sessionCost.reasoningTokens}
            compact
          />
        )}
      </Box>
    </Box>
  )
}

// Memoized public alias. The composer above ChatCanvas updates its local
// `composerValue` state on every keystroke, which re-renders ChatCanvas and
// would otherwise re-render every MessageBubble — each of which renders an
// AssistantMarkdown that re-parses the assistant prose. The custom comparator
// below short-circuits those re-renders by comparing only the props that can
// actually change the bubble's visible output. The {@code sessionCost} object
// is rebuilt every parent render, so we shallow-compare its primitive fields
// instead of relying on referential equality. {@code onRevoke} is created
// inline at the call site and so always differs by reference — but it's only
// invoked from a hover affordance and not part of the rendered output, so we
// deliberately exclude it from the comparator (the latest closure is still
// captured by virtue of MessageBubble being re-created if any other prop did
// change).
function sessionCostsEqual(
  a: MessageBubbleProps['sessionCost'],
  b: MessageBubbleProps['sessionCost'],
): boolean {
  if (a === b) return true
  if (!a || !b) return false
  return (
    a.costUsd === b.costUsd &&
    a.inputTokens === b.inputTokens &&
    a.outputTokens === b.outputTokens &&
    a.cacheReadTokens === b.cacheReadTokens &&
    a.cacheWriteTokens === b.cacheWriteTokens &&
    a.reasoningTokens === b.reasoningTokens
  )
}

const MessageBubble = memo(MessageBubbleImpl, (prev, next) => {
  if (prev.message.id !== next.message.id) return false
  if (prev.message.text !== next.message.text) return false
  if (prev.message.kind !== next.message.kind) return false
  if (prev.message.createdAt !== next.message.createdAt) return false
  if (prev.isQueued !== next.isQueued) return false
  if (prev.modelLabel !== next.modelLabel) return false
  if (prev.modelSlug !== next.modelSlug) return false
  // onRevoke identity is unstable (inline arrow at the call site) but does
  // not affect rendering — skip it on purpose. If onRevoke becomes part of
  // the bubble's reactive surface, lift it to a useCallback in the parent.
  if (!sessionCostsEqual(prev.sessionCost, next.sessionCost)) return false
  return true
})

// ── ChatCanvas ─────────────────────────────────────────────────────────────

/**
 * ChatCanvas v2 (P3.1) — the calm transcript surface inside the project
 * workspace canvas. Replaces the legacy {@code ChatPanel} when the runtime
 * is Online.
 *
 * <p>Responsibilities:
 * <ol>
 *   <li>Load conversation history from the persisted event store — one fetch
 *       per session in {@code ConversationDetail.sessions}, merged into a
 *       single ordered transcript.</li>
 *   <li>Subscribe to live {@code agentEvent} pushes from the AgentHub and
 *       append-with-dedupe by {@code sessionId:sequence}.</li>
 *   <li>Render user / assistant bubbles with equal visual weight; collapse
 *       all tool + thinking + lifecycle events into one
 *       {@link TurnStatusLine} per turn (P3.2). P3.3 will replace the
 *       expand stub with the real inline trace.</li>
 *   <li>Host the composer — Enter sends, Shift+Enter newline, disabled while
 *       the wire is down or the latest session is still pending/running.</li>
 *   <li>Empty state when {@code conversationId} is {@code null}: a generous
 *       centered composition with the project name + composer, no
 *       transcript above.</li>
 * </ol></p>
 *
 * <p>The component does NOT own the SignalR connection — the parent route
 * lifts that exactly once via {@code useAgentHub} and threads it down here.
 * The component does NOT navigate either: when a new conversation is created
 * (from the empty state) it updates the {@code ?c=} search param in place so
 * the route stays mounted and the chrome / sidebar pick up the new id.</p>
 */

// ── Error message extraction ─────────────────────────────────────────────────
//
// SignalR wraps server-thrown {@code HubException}s with a noisy preamble:
//   "An unexpected error occurred invoking 'SubmitPrompt' on the server.
//    HubException: <the actual message>"
// Map the daemon's coarse commit-failure reason to a human-readable next step.
// Used by both the transient toast and the persistent out-of-sync banner.
function commitReasonHint(reason: string): string {
  switch (reason) {
    case 'Identity':
      return "Git couldn't find a committer identity — the runtime image is misconfigured. Report this to support."
    case 'Hook':
      return 'A pre-commit hook rejected the change — open the runtime terminal and inspect the hook output.'
    case 'Lock':
      return 'Another git process is holding the index lock — retry in a moment.'
    case 'Timeout':
      return 'The git process was killed. Likely a hook hung or the runtime ran out of memory.'
    default:
      return 'Open the runtime terminal and run `git status` to investigate.'
  }
}

// Map the daemon's coarse push-failure reason to a human-readable next step.
function pushReasonHint(reason: string): string {
  switch (reason) {
    case 'Auth':
      return 'The runtime cannot authenticate with GitHub. Re-run repo provisioning to refresh credentials.'
    case 'Network':
      return 'Network couldn’t reach GitHub. Will retry automatically.'
    case 'Conflict':
      return 'The remote has commits we don’t — pull first, then retry.'
    default:
      return 'Open the runtime terminal and run `git push` to investigate.'
  }
}

// Events that exist in {@code eventsBySession} but don't represent visible
// agent "work" — the user's prompt (rendered as a separate user bubble) and
// lifecycle Status transitions (Creating / Running / Finished / Error /
// Cancelled / Expired — surfaced as a single activity pill in card 6).
// They're kept in the raw stream for debugging / future trace surfaces, but
// they must NOT form phases on their own: a turn whose only extra events are
// a {@code PromptReceived} and a {@code Status(Running)} should render as
// exactly one assistant bubble with no status rows around it. Filter them
// out of {@link splitTurnIntoPhases} so the temporal rhythm is driven purely
// by real agent activity (thinking, tool calls) and assistant text.
const PHASE_NOISE_EVENT_KINDS: ReadonlySet<string> = new Set<string>([
  'promptReceived',
  'status',
  'task',
])

// ── Phase splitting ────────────────────────────────────────────────────────
//
// A "phase" is one act of the turn — the work the agent did between two
// consecutive assistant messages (or between the turn start and the first
// message, or between the last message and the turn end). Splitting a turn
// into phases gives the transcript an accurate temporal rhythm:
//
//   user prompt
//   ▸ Reading files, searching, thinking…       ← phase 1 events
//   "I'll start by mapping the auth module."     ← phase 1 closing bubble(s)
//   ▸ Editing files, running shell…              ← phase 2 events
//   "Refactor done. Running tests."              ← phase 2 closing bubble(s)
//   ▸ Running shell, reading output…             ← phase 3 events
//   "All 47 tests pass."                          ← phase 3 closing bubble(s)
//
// Consecutive {@code AssistantText} events coalesce into one phase boundary —
// otherwise short multi-paragraph replies would emit empty "Done" rows
// between every paragraph. The pairing between events and bubbles is by
// {@code sequence} (assistant bubbles in {@code group.assistantBubbles} have
// the same sequence as their originating {@code AssistantText} event).
interface TurnPhase {
  /** Non-text events that ran before this phase's closing bubble(s). */
  events: AgentEventDto[]
  /** The {@code AssistantText}-derived bubbles that close this phase. */
  bubbles: Extract<Message, { kind: 'assistant' }>[]
  /**
   * {@code true} when a later phase exists — meaning the work in this phase
   * is sealed and the status line should render as "Done" regardless of the
   * session's live status. Only the LAST phase reflects the session's
   * Pending / Running / Failed / Canceled state.
   */
  isClosed: boolean
}

function splitTurnIntoPhases(
  events: AgentEventDto[],
  assistantBubbles: Extract<Message, { kind: 'assistant' }>[],
): TurnPhase[] {
  const bubbleBySequence = new Map<number, Extract<Message, { kind: 'assistant' }>>()
  for (const b of assistantBubbles) bubbleBySequence.set(b.sequence, b)

  const phases: TurnPhase[] = []
  let curEvents: AgentEventDto[] = []
  let curBubbles: Extract<Message, { kind: 'assistant' }>[] = []
  // Tracks whether we've seen at least one bubble since the last phase
  // flush — the next non-text event then closes the current phase.
  let inBubblesMode = false

  for (const evt of events) {
    if (evt.eventKind === 'assistantText') {
      const bubble = bubbleBySequence.get(evt.sequence)
      if (bubble) {
        // Coalesce consecutive AssistantText events into a single bubble.
        // The Cursor SDK streams text in deltas — each frame yields a tiny
        // content block, so a naive per-event-bubble policy renders a normal
        // markdown response as ~200 mini-bubbles stacked vertically (each
        // delta becomes its own visual bubble with newlines between).
        // Claude / Opencode deliver fully-assembled message blocks, so they
        // never tripped this. Merge here, AFTER phase splitting has decided
        // what "consecutive" means (a non-text event breaks the run and
        // starts a new bubble in the next phase), so a turn that fires
        //   text "Let me check." → ToolUse → text "Based on this, " → text "done."
        // still renders as TWO bubbles (one before the tool, one after) —
        // just collapsed within each consecutive run instead of N per delta.
        //
        // Construct a NEW Message object rather than mutating — these bubbles
        // are stored in React state (historyById) and mutating them would
        // break reconciliation across renders. The merged bubble keeps the
        // FIRST event's id / sequence so React reuses the same DOM node as
        // the bubble grows during streaming (rather than re-mounting and
        // re-parsing markdown on every delta).
        if (curBubbles.length > 0) {
          const head = curBubbles[curBubbles.length - 1]
          curBubbles[curBubbles.length - 1] = {
            ...head,
            text: head.text + bubble.text,
          }
        } else {
          curBubbles.push(bubble)
        }
        inBubblesMode = true
      }
      continue
    }
    // Skip noise (the user's prompt + lifecycle Status / Task transitions).
    // They stay in {@code eventsBySession} for the activity pill etc. but
    // shouldn't drive the visible phase rhythm.
    if (PHASE_NOISE_EVENT_KINDS.has(evt.eventKind)) continue
    if (inBubblesMode) {
      // Closing the current phase: events + bubbles we've accumulated, with
      // the {@code isClosed} flag set so its status line shows "Done".
      phases.push({ events: curEvents, bubbles: curBubbles, isClosed: true })
      curEvents = []
      curBubbles = []
      inBubblesMode = false
    }
    curEvents.push(evt)
  }

  // Trailing phase — whatever's left after the last non-text event (or after
  // the last bubble if the turn ended with text). Always {@code isClosed:
  // false} so the last phase reflects the live session status.
  if (curEvents.length > 0 || curBubbles.length > 0) {
    phases.push({ events: curEvents, bubbles: curBubbles, isClosed: false })
  }

  // Ensure at least one phase exists so the soft-queue / warming status line
  // has a row to render into even before any events have arrived.
  if (phases.length === 0) {
    phases.push({ events: [], bubbles: [], isClosed: false })
  }

  return phases
}

// Surface just the actionable part to the user. Fall back to the raw message
// when the marker is missing so we never end up with an empty toast.
function extractSubmitErrorMessage(err: unknown): string {
  const raw =
    err instanceof Error
      ? err.message
      : typeof err === 'string'
        ? err
        : 'Failed to send message.'
  const marker = 'HubException:'
  const idx = raw.indexOf(marker)
  if (idx >= 0) {
    const tail = raw.slice(idx + marker.length).trim()
    if (tail.length > 0) return tail
  }
  return raw
}

// ── PhaseChromeWithRunResult ────────────────────────────────────────────────
//
// Thin wrapper around {@link TurnPhaseChrome} that conditionally fetches the
// session-level {@link RunResultDto} via Orval and threads it down. The
// RunResult drives the footer's duration/model/artifacts/PR row (scene 6).
//
// The fetch only fires when {@code shouldQueryRunResult} is true — usually
// "this is the last phase of a terminal turn" — so we never poll RunResult
// for in-flight turns where the daemon hasn't emitted it yet. When the
// RunResult is unavailable (404, not yet emitted) {@link TurnFooter}
// degrades gracefully to a verb-only line ("Finished").
//
// React hooks can't be conditional, so the parent ChatCanvas can't call
// {@code useGetApiSessionsIdRunResult} inline inside its phase map — it
// needs a stable child component per phase. This wrapper provides that.
interface PhaseChromeWithRunResultProps {
  sessionId: string
  events: AgentEventDto[]
  isLiveTurn: boolean
  isLastPhase: boolean
  terminalStatus: TerminalRunStatus | null
  shouldQueryRunResult: boolean
  onCancel?: () => void
  optimisticCancelling?: boolean
}

function PhaseChromeWithRunResult({
  sessionId,
  events,
  isLiveTurn,
  isLastPhase,
  terminalStatus,
  shouldQueryRunResult,
  onCancel,
  optimisticCancelling,
}: PhaseChromeWithRunResultProps) {
  // The query is gated by {@code enabled} so the network call only fires
  // when we know the turn is terminal. 404s are expected (older turns
  // predate RunResult plumbing) — the footer handles {@code null} cleanly.
  const runResultQuery = useGetApiSessionsIdRunResult(sessionId, {
    query: {
      enabled: shouldQueryRunResult,
      // Treat 404 as a non-error — the footer simply omits the rich pieces.
      retry: false,
    },
  })
  return (
    <TurnPhaseChrome
      sessionId={sessionId}
      events={events}
      isLiveTurn={isLiveTurn}
      isLastPhase={isLastPhase}
      terminalStatus={terminalStatus}
      runResult={runResultQuery.data ?? null}
      onCancel={onCancel}
      optimisticCancelling={optimisticCancelling}
    />
  )
}

// ── SessionPlaceholderTurn ──────────────────────────────────────────────────
//
// Lazy-session-hydration: a lightweight stand-in for an older session whose
// full event transcript hasn't been drained yet. It shows the user's original
// prompt (so Priya can recognize the turn she's scrolling back to) plus a
// quiet "loading…" hint, WITHOUT mounting the session's event list / markdown.
//
// An IntersectionObserver on the outer node triggers {@code onHydrate} the
// moment the placeholder scrolls into view (with a generous rootMargin so the
// real content is usually ready by the time the user's eyes land). Hydration
// is sticky upstream (the hydrated-set ref never reverts), so we fire at most
// once per placeholder and disconnect immediately after.
//
// The reserved {@code minHeight} keeps scroll position stable: the box claims
// plausible vertical room up-front so swapping the placeholder for the real
// (usually taller) content doesn't yank the viewport.
interface SessionPlaceholderTurnProps {
  sessionId: string
  prompt: string | null | undefined
  onHydrate: (sessionId: string) => void
}

function SessionPlaceholderTurnImpl({
  sessionId,
  prompt,
  onHydrate,
}: SessionPlaceholderTurnProps) {
  const nodeRef = useRef<HTMLDivElement | null>(null)
  const firedRef = useRef(false)

  useEffect(() => {
    const node = nodeRef.current
    if (!node) return
    // Fallback for environments without IntersectionObserver (older test
    // harnesses / jsdom): hydrate immediately so content is never stranded.
    if (typeof IntersectionObserver === 'undefined') {
      if (!firedRef.current) {
        firedRef.current = true
        onHydrate(sessionId)
      }
      return
    }
    const observer = new IntersectionObserver(
      (entries) => {
        const entry = entries[0]
        if (entry?.isIntersecting && !firedRef.current) {
          firedRef.current = true
          observer.disconnect()
          onHydrate(sessionId)
        }
      },
      // Start the drain a little before the placeholder is fully on screen so
      // the transcript is ready as the user arrives.
      { rootMargin: '600px 0px', threshold: 0 },
    )
    observer.observe(node)
    return () => observer.disconnect()
  }, [sessionId, onHydrate])

  return (
    <Box
      ref={nodeRef}
      data-session-placeholder={sessionId}
      sx={{
        display: 'flex',
        flexDirection: 'column',
        gap: 0.75,
        // Reserve plausible room so the scroll position stays put when the
        // real (taller) transcript fills in.
        minHeight: 96,
        animation: `${fadeIn} 200ms ease`,
      }}
    >
      {/* Prompt bubble — mirrors the user-bubble chrome (right-aligned, rounded
          surface) so the placeholder reads as the same turn it will become. */}
      {prompt ? (
        <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
          <Box
            sx={{
              maxWidth: '78%',
              px: 1.75,
              py: 1.25,
              borderRadius: '14px',
              backgroundColor: tokens.bubbleUser,
              color: tokens.textPrimary,
              fontFamily: workspaceFontFamily,
              fontSize: '0.9375rem',
              lineHeight: 1.5,
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              // Clamp very long prompts so the placeholder stays compact.
              display: '-webkit-box',
              WebkitLineClamp: 4,
              WebkitBoxOrient: 'vertical',
              overflow: 'hidden',
            }}
          >
            {prompt}
          </Box>
        </Box>
      ) : null}
      {/* Quiet loading hint — nice-to-have feedback that the transcript is on
          its way as the turn group scrolls into view. */}
      <Box sx={{ display: 'flex', justifyContent: 'flex-start' }}>
        <Typography
          sx={{
            fontFamily: workspaceFontFamily,
            fontSize: '0.75rem',
            color: tokens.textFaint,
            fontStyle: 'italic',
          }}
        >
          loading…
        </Typography>
      </Box>
    </Box>
  )
}

const SessionPlaceholderTurn = memo(SessionPlaceholderTurnImpl)

// ── LoadEarlierSentinel ─────────────────────────────────────────────────────
//
// A top-of-turn affordance for paging BACKWARD into a hydrated session's
// history. Tail-first hydration loads the NEWEST window; scrolling UP to this
// sentinel fetches the page of OLDER events immediately preceding the loaded
// window and PREPENDS it (scroll position is preserved by the caller).
//
// The sentinel auto-triggers when it scrolls into view (IntersectionObserver,
// generous rootMargin so the older page is usually ready before the top edge
// is reached). A clickable button is rendered as the visible surface AND as the
// fallback for environments without IntersectionObserver. The observer re-arms
// after each load (unlike the placeholder's one-shot) so successive scroll-ups
// keep paging until the session reports no more older events.
interface LoadEarlierSentinelProps {
  sessionId: string
  loading: boolean
  onLoadEarlier: (sessionId: string) => void
}

function LoadEarlierSentinelImpl({
  sessionId,
  loading,
  onLoadEarlier,
}: LoadEarlierSentinelProps) {
  const nodeRef = useRef<HTMLDivElement | null>(null)
  // Keep the latest callback/loading in refs so the observer effect doesn't
  // re-create (and thus re-fire) on every parent render.
  const onLoadRef = useRef(onLoadEarlier)
  const loadingRef = useRef(loading)
  onLoadRef.current = onLoadEarlier
  loadingRef.current = loading

  useEffect(() => {
    const node = nodeRef.current
    if (!node) return
    if (typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(
      (entries) => {
        const entry = entries[0]
        if (entry?.isIntersecting && !loadingRef.current) {
          onLoadRef.current(sessionId)
        }
      },
      // Begin loading a little before the user reaches the very top so the
      // older page is ready as they arrive.
      { rootMargin: '400px 0px', threshold: 0 },
    )
    observer.observe(node)
    return () => observer.disconnect()
  }, [sessionId])

  return (
    <Box
      ref={nodeRef}
      data-load-earlier={sessionId}
      sx={{ display: 'flex', justifyContent: 'center', py: 0.5 }}
    >
      <Box
        component="button"
        type="button"
        disabled={loading}
        onClick={() => onLoadEarlier(sessionId)}
        sx={{
          background: 'none',
          border: `1px solid ${tokens.hairline}`,
          borderRadius: '999px',
          padding: '4px 12px',
          cursor: 'pointer',
          fontSize: 12.5,
          fontFamily: 'inherit',
          color: tokens.textMuted,
          transition:
            'color 200ms ease, background-color 200ms ease, border-color 200ms ease',
          '&:hover': {
            color: tokens.textPrimary,
            backgroundColor: 'rgba(0, 0, 0, 0.02)',
            borderColor: 'rgba(0, 0, 0, 0.18)',
          },
          '&:disabled': {
            cursor: 'default',
            opacity: 0.6,
          },
        }}
      >
        {loading ? 'Loading…' : 'Load earlier'}
      </Box>
    </Box>
  )
}

const LoadEarlierSentinel = memo(LoadEarlierSentinelImpl)

export function ChatCanvas({
  projectId,
  branchId,
  conversationId,
  connection,
  runtimeState,
  onComposerFocusChange,
}: ChatCanvasProps) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const { slug = '' } = useParams<{ slug: string }>()
  const { showError } = useNotification()
  void navigate // imported for future use; setSearchParams keeps us in place.

  // ── Empty-state metadata (project + branch names) ────────────────────────
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })

  // ── Per-turn model pill — resolve the Cursor model slug for each user
  //     bubble in the transcript.
  const cursorModelsQuery = useGetApiCursorModelsActive({
    query: { staleTime: 30_000 },
  })
  const cursorModelsById = useMemo(() => {
    const map = new Map<string, CursorModelDto>()
    for (const m of cursorModelsQuery.data ?? []) map.set(m.id, m)
    return map
  }, [cursorModelsQuery.data])
  // Walk a potentially-richer session shape — the backend may add these
  // fields without bumping the TypeScript type and we don't want to wait
  // for a regen to render the pill. The cast is intentional and one-way.
  const resolveSessionModel = useCallback(
    (
      session: SessionSummary | null | undefined,
    ): { label: string | null; slug: string | null } => {
      if (!session) return { label: null, slug: null }
      const wide = session as SessionSummary & {
        agentModelId?: string | null
        agentModelSlug?: string | null
      }
      const slug =
        typeof wide.agentModelSlug === 'string' && wide.agentModelSlug.length > 0
          ? wide.agentModelSlug
          : null
      const id =
        typeof wide.agentModelId === 'string' && wide.agentModelId.length > 0
          ? wide.agentModelId
          : null
      if (id) {
        const hit = cursorModelsById.get(id)
        if (hit) return { label: hit.displayName, slug: hit.slug }
      }
      if (slug) {
        for (const m of cursorModelsById.values()) {
          if (m.slug === slug) return { label: m.displayName, slug: m.slug }
        }
        return { label: slug, slug }
      }
      return { label: null, slug: null }
    },
    [cursorModelsById],
  )
  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    undefined,
    {
      query: { enabled: !!projectId },
    },
  )
  const projectName = projectQuery.data?.name ?? 'this project'
  const branchName = useMemo(() => {
    const list = branchesQuery.data ?? []
    const match = list.find((b) => b.id === branchId)
    return match?.name ?? projectQuery.data?.defaultBranchName ?? 'this branch'
  }, [branchesQuery.data, branchId, projectQuery.data?.defaultBranchName])

  // ── Conversation detail (sessions) ───────────────────────────────────────
  const conversationQuery = useGetApiConversationsId(conversationId ?? '', {
    query: { enabled: !!conversationId },
  })
  const sessions: SessionSummary[] = useMemo(
    () => conversationQuery.data?.sessions ?? [],
    [conversationQuery.data?.sessions],
  )

  // ── History fetch ─────────────────────────────────────────────────────────
  //
  // The Orval hook for /api/sessions/:id/events only loads ONE session at a
  // time, but a conversation can fan out across multiple sessions. We do the
  // join manually with the raw {@code getApiSessionsIdEvents} fetcher — one
  // call per session, paged forward in lockstep until each session is fully
  // drained. Results are flattened into a single map keyed by
  // {@code sessionId:sequence} so live events merge in via the same map.
  const [historyById, setHistoryById] = useState<Map<string, Message>>(new Map())
  // Parallel store of raw events keyed by `sessionId:sequence`. The status
  // line derives its label from the latest event for each turn, so we keep
  // the raw row around regardless of whether {@link eventToMessage} produced
  // a visible bubble. (Status-kind messages are no longer rendered after P3.2 —
  // we render exactly one {@link TurnStatusLine} per turn instead.)
  const [rawEventsById, setRawEventsById] = useState<Map<string, AgentEventDto>>(
    new Map(),
  )
  const [historyLoading, setHistoryLoading] = useState(false)
  // Tracks which conversation the current history map belongs to — so we wipe
  // cleanly when the user switches conversations from the sidebar.
  const historyConversationRef = useRef<string | null>(null)
  // Per-session drain progress so the hydration effect is INCREMENTAL rather
  // than re-draining every session from {@code since=0} each time the
  // conversation detail query changes. Keyed by sessionId → the highest event
  // sequence we've already pulled. A status-only change to the conversation
  // (e.g. a session flips Running→Succeeded) no longer triggers a full refetch
  // storm — we only fetch sessions we haven't drained yet, plus a short
  // top-up for sessions still genuinely in-flight. Cleared on conversation
  // switch alongside the history maps.
  const drainedSessionsRef = useRef<Map<string, number>>(new Map())
  // Per-session LOWEST loaded sequence — the TOP of the currently-loaded
  // window. Tail-first hydration loads the NEWEST events first, so the lowest
  // loaded sequence is the boundary the "Load earlier" backward page resumes
  // from ({@code before: lowestLoaded} returns the page of older events
  // immediately preceding what we already have, ascending). Cleared on
  // conversation switch alongside the history maps.
  const lowestLoadedRef = useRef<Map<string, number>>(new Map())
  // Lazy-session hydration (lazy-session-hydration-in-the-transcript spec):
  // on mount we eager-drain ONLY the active session(s); every older session
  // starts as a lightweight prompt placeholder and hydrates its full event
  // transcript when its turn group scrolls into view. This set tracks which
  // session ids have graduated from placeholder → hydrated. Membership is
  // STICKY for the life of the view: once a session is here it never reverts
  // to a placeholder (no thrash on scroll back-and-forth). Cleared on
  // conversation switch alongside the history maps. The companion
  // {@code hydratedVersion} state bumps to force the render-time memos
  // ({@code visibleTurnGroups} placeholdering, {@code shouldQueryRunResult})
  // to recompute when the set mutates — refs alone don't trigger renders.
  const hydratedSessionsRef = useRef<Set<string>>(new Set())
  const [hydratedVersion, setHydratedVersion] = useState(0)
  // Per-session "there are OLDER events" flag, driven off the last loaded page
  // size: a full page (=== 500) means the session likely has more events BEFORE
  // the current top-of-window, so we surface a "Load earlier" affordance at the
  // TOP of the turn that pages BACKWARD ({@code before: lowestLoaded}, ascending)
  // and PREPENDS the older chunk. A short page clears the flag — we've reached
  // the start of the session. State (not a ref) so the affordance re-renders
  // when it changes.
  const [hasMoreBySession, setHasMoreBySession] = useState<Set<string>>(
    new Set(),
  )
  const [loadingMoreSessions, setLoadingMoreSessions] = useState<Set<string>>(
    new Set(),
  )
  const PAGE_LIMIT = 500
  // Backward "tail" cursor: the endpoint returns up to {@code limit} events
  // with {@code Sequence < before}, ascending. {@code Number.MAX_SAFE_INTEGER}
  // therefore yields the latest (newest) page of the session.
  const TAIL_CURSOR = Number.MAX_SAFE_INTEGER

  useEffect(() => {
    const prev = historyConversationRef.current
    if (prev === conversationId) return
    historyConversationRef.current = conversationId

    // Empty-state → first-send case: we just minted this conversation id.
    // The optimistic user bubble in state IS the conversation right now;
    // wiping it would create the disappear→reappear flash the user
    // reported. Leave the in-memory state alone — the upcoming history
    // hydration effect will merge persisted sessions on top without
    // clobbering the optimistic bubble (it gets dropped automatically by
    // the reconcile effect once the persisted twin shows up).
    if (prev === null && conversationId !== null) return

    // Real conversation switch (or back to empty state) — wipe.
    setHistoryById(new Map())
    setRawEventsById(new Map())
    setOptimistic([])
    drainedSessionsRef.current = new Map()
    lowestLoadedRef.current = new Map()
    hydratedSessionsRef.current = new Set()
    setHydratedVersion((v) => v + 1)
    setHasMoreBySession(new Set())
    setLoadingMoreSessions(new Set())
  }, [conversationId])

  // Reconcile optimistic user bubbles against the persisted sessions list.
  // When a session whose `prompt` matches an outstanding optimistic bubble
  // shows up (after the conversation detail invalidation post-submit), drop
  // the optimistic twin — the synthetic user bubble derived from
  // `session.prompt` takes over.
  useEffect(() => {
    if (sessions.length === 0) return
    setOptimistic((prev) => {
      if (prev.length === 0) return prev
      const persisted = new Set(sessions.map((s) => (s.prompt ?? '').trim()))
      const next = prev.filter((m) => !persisted.has(m.text.trim()))
      return next.length === prev.length ? prev : next
    })
  }, [sessions])

  // Stable signature of the session set: sorted "id:status" pairs. This is the
  // effect's dependency instead of the whole {@code conversationQuery.data}
  // object (which is a fresh reference on every refetch) or the {@code
  // sessions} array (also a fresh reference). A pure status flip changes this
  // string, but the incremental effect body below skips already-drained,
  // terminal sessions — so a status-only change is a cheap no-op rather than a
  // full re-drain of every session from {@code since=0}.
  const sessionsSignature = useMemo(
    () =>
      sessions
        .map((s) => `${s.id}:${s.status}`)
        .sort()
        .join('|'),
    [sessions],
  )

  // The "active" session(s) are exempt from placeholdering and are eager-drained
  // on mount: the single newest session by {@code createdAt} (the most recent
  // turn the user just landed on), plus any session still genuinely in flight
  // (Running / Pending / Canceling) — an in-flight older session is rare but
  // must stay live. Everything else starts as a lightweight placeholder and
  // hydrates on scroll-into-view. Returned as a Set of session ids for O(1)
  // membership checks in the drain effect and the render map.
  const activeSessionIds = useMemo(() => {
    const ids = new Set<string>()
    if (sessions.length === 0) return ids
    const newest = [...sessions].sort((a, b) =>
      b.createdAt.localeCompare(a.createdAt),
    )[0]
    if (newest) ids.add(newest.id)
    for (const s of sessions) {
      if (
        s.status === AgentSessionStatus.Running ||
        s.status === AgentSessionStatus.Pending ||
        s.status === AgentSessionStatus.Canceling
      ) {
        ids.add(s.id)
      }
    }
    return ids
  }, [sessions])

  // ── Scroll refs (declared early for scroll-position preservation) ────────
  // The sticky-when-at-bottom hook owns the scroll container ref. We pull it
  // up here so {@code handleLoadEarlier} can capture {@code scrollRef.current}
  // and restore scrollTop after prepending an older page (see below). The
  // remaining scroll-behavior wiring lives further down where it's consumed.
  const bottomRef = useRef<HTMLDivElement | null>(null)
  const { scrollRef, contentRef, isAtBottom, scrollToBottom } =
    useStickToBottom<HTMLDivElement>({ resetKey: conversationId ?? '' })

  // Single-session TAIL hydration. Used both for the eager pass on mount (the
  // active session) and for on-scroll hydration of a placeholder session.
  // Fetches the NEWEST page ({@code before: TAIL_CURSOR}, ascending) so the
  // latest events show at the bottom in chat order. Records BOTH the highest
  // sequence seen (forward cursor for live in-flight top-up) and the lowest
  // sequence seen (top-of-window cursor for the backward "Load earlier" page).
  // Older events are loaded on scroll-up via {@code handleLoadEarlier} — there
  // is no forward auto-drain here.
  const hydrateSession = useCallback(
    async (sessionId: string) => {
      const session = sessions.find((s) => s.id === sessionId)
      // Revoked-before-start sessions have no transcript surface — skip.
      if (
        session &&
        session.status === AgentSessionStatus.Canceled &&
        !session.startedAt
      ) {
        hydratedSessionsRef.current.add(sessionId)
        setHydratedVersion((v) => v + 1)
        return
      }
      const collected = new Map<string, Message>()
      const rawCollected = new Map<string, AgentEventDto>()
      // Synthetic prompt fallback (legacy sessions with `session.prompt` but no
      // PromptReceived event). Seeded on the FIRST hydration only.
      if (session?.prompt) {
        const syntheticUserId = `session:${session.id}:prompt`
        collected.set(syntheticUserId, {
          kind: 'user',
          id: syntheticUserId,
          sessionId: session.id,
          sequence: 0,
          text: session.prompt,
        })
      }
      let highest = drainedSessionsRef.current.get(sessionId) ?? 0
      let lowest = lowestLoadedRef.current.get(sessionId) ?? Number.MAX_SAFE_INTEGER
      let pageLen = 0
      try {
        // TAIL page: events with Sequence < TAIL_CURSOR, ascending — i.e. the
        // newest window of the session.
        const page = await getApiSessionsIdEvents(sessionId, {
          before: TAIL_CURSOR,
          limit: PAGE_LIMIT,
        })
        pageLen = page?.length ?? 0
        for (const raw of page ?? []) {
          const msg = eventToMessage(raw)
          if (msg) collected.set(msg.id, msg)
          rawCollected.set(`${raw.sessionId}:${raw.sequence}`, raw)
          highest = Math.max(highest, raw.sequence)
          lowest = Math.min(lowest, raw.sequence)
        }
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[ChatCanvas] failed to load session events', sessionId, err)
      }
      drainedSessionsRef.current.set(sessionId, highest)
      if (lowest !== Number.MAX_SAFE_INTEGER) {
        lowestLoadedRef.current.set(
          sessionId,
          Math.min(lowestLoadedRef.current.get(sessionId) ?? lowest, lowest),
        )
      }
      // A full page means there are likely OLDER events before the top of the
      // window — surface "Load earlier"; a short page clears it.
      setHasMoreBySession((prev) => {
        const has = prev.has(sessionId)
        const shouldHave = pageLen >= PAGE_LIMIT
        if (has === shouldHave) return prev
        const next = new Set(prev)
        if (shouldHave) next.add(sessionId)
        else next.delete(sessionId)
        return next
      })
      if (collected.size > 0) {
        setHistoryById((prev) => {
          const next = new Map(prev)
          for (const [k, v] of collected) next.set(k, v)
          return next
        })
      }
      if (rawCollected.size > 0) {
        setRawEventsById((prev) => {
          const next = new Map(prev)
          for (const [k, v] of rawCollected) next.set(k, v)
          return next
        })
      }
      hydratedSessionsRef.current.add(sessionId)
      setHydratedVersion((v) => v + 1)
    },
    [sessions],
  )

  // Stable scroll-into-view hydration trigger handed to each placeholder turn
  // group's IntersectionObserver. Guards against re-firing a session that's
  // already hydrated (the placeholder also self-guards via firedRef, but this
  // keeps the contract clean if the same session is observed twice).
  const handleHydrateSession = useCallback(
    (sessionId: string) => {
      if (hydratedSessionsRef.current.has(sessionId)) return
      void hydrateSession(sessionId)
    },
    [hydrateSession],
  )

  // Scroll-position preservation for "Load earlier". Prepending older events
  // grows the scroll content at the TOP, which would otherwise yank the
  // viewport upward. We snapshot the container's {@code scrollHeight} +
  // {@code scrollTop} immediately before the prepend, then a layout effect
  // restores {@code scrollTop += (newScrollHeight - oldScrollHeight)} after the
  // DOM grows so the user's reading position stays put. A bump counter keys the
  // layout effect; {@code null} means "no pending restore".
  const loadEarlierAnchorRef = useRef<{
    scrollHeight: number
    scrollTop: number
  } | null>(null)
  const [loadEarlierTick, setLoadEarlierTick] = useState(0)

  useLayoutEffect(() => {
    if (loadEarlierTick === 0) return
    const anchor = loadEarlierAnchorRef.current
    const el = scrollRef.current
    loadEarlierAnchorRef.current = null
    if (!anchor || !el) return
    const delta = el.scrollHeight - anchor.scrollHeight
    if (delta !== 0) {
      el.scrollTop = anchor.scrollTop + delta
    }
  }, [loadEarlierTick, scrollRef])

  // "Load earlier" — page BACKWARD from the top of the loaded window. Fetches
  // up to one page of events with {@code Sequence < lowestLoaded} (ascending)
  // and merges them into the history maps; the sequence-sorted turn memos place
  // them ABOVE the existing events (a visual prepend). Scroll position is
  // preserved via the anchor/layout-effect above. A short page (< PAGE_LIMIT)
  // means we've reached the start of the session, so the affordance is cleared.
  // Guards against concurrent clicks via the loading set.
  const handleLoadEarlier = useCallback(
    async (sessionId: string) => {
      if (loadingMoreSessions.has(sessionId)) return
      const lowest = lowestLoadedRef.current.get(sessionId)
      // Nothing loaded yet (shouldn't happen — only hydrated sessions show the
      // affordance) or already at the very start.
      if (lowest === undefined || lowest <= 0) {
        setHasMoreBySession((prev) => {
          if (!prev.has(sessionId)) return prev
          const next = new Set(prev)
          next.delete(sessionId)
          return next
        })
        return
      }
      setLoadingMoreSessions((prev) => {
        const next = new Set(prev)
        next.add(sessionId)
        return next
      })
      // Snapshot scroll metrics BEFORE the prepend so the layout effect can
      // restore the viewport once the older events grow the content.
      const el = scrollRef.current
      if (el) {
        loadEarlierAnchorRef.current = {
          scrollHeight: el.scrollHeight,
          scrollTop: el.scrollTop,
        }
      }
      const collected = new Map<string, Message>()
      const rawCollected = new Map<string, AgentEventDto>()
      let nextLowest = lowest
      let pageLen = 0
      try {
        const page = await getApiSessionsIdEvents(sessionId, {
          before: lowest,
          limit: PAGE_LIMIT,
        })
        pageLen = page?.length ?? 0
        for (const raw of page ?? []) {
          const msg = eventToMessage(raw)
          if (msg) collected.set(msg.id, msg)
          rawCollected.set(`${raw.sessionId}:${raw.sequence}`, raw)
          nextLowest = Math.min(nextLowest, raw.sequence)
        }
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[ChatCanvas] failed to load earlier events', sessionId, err)
      } finally {
        lowestLoadedRef.current.set(sessionId, nextLowest)
        setHasMoreBySession((prev) => {
          const has = prev.has(sessionId)
          const shouldHave = pageLen >= PAGE_LIMIT
          if (has === shouldHave) return prev
          const next = new Set(prev)
          if (shouldHave) next.add(sessionId)
          else next.delete(sessionId)
          return next
        })
        if (collected.size > 0) {
          setHistoryById((prev) => {
            const next = new Map(prev)
            for (const [k, v] of collected) next.set(k, v)
            return next
          })
        }
        if (rawCollected.size > 0) {
          setRawEventsById((prev) => {
            const next = new Map(prev)
            for (const [k, v] of rawCollected) next.set(k, v)
            return next
          })
        }
        // Bump the tick so the layout effect runs the scroll-position restore
        // after the prepended events have grown the DOM.
        setLoadEarlierTick((t) => t + 1)
        setLoadingMoreSessions((prev) => {
          if (!prev.has(sessionId)) return prev
          const next = new Set(prev)
          next.delete(sessionId)
          return next
        })
      }
    },
    [loadingMoreSessions, scrollRef],
  )

  useEffect(() => {
    if (!conversationId) return
    if (!conversationQuery.data) return
    let cancelled = false
    const ordered = [...sessions].sort((a, b) =>
      a.createdAt.localeCompare(b.createdAt),
    )
    // LAZY HYDRATION (lazy-session-hydration-in-the-transcript spec):
    // the eager pass NO LONGER drains every session. It drains ONLY:
    //   * the active session(s) — the newest turn + anything still in-flight —
    //     on first sight (so the live view is instant and fully rendered), and
    //   * an already-hydrated session that is still in-flight, to top it up
    //     from its last-drained sequence so out-of-band live progress lands.
    // Every OTHER (older) session is left UNHYDRATED — it renders as a
    // lightweight prompt placeholder and only drains when its turn group
    // scrolls into view (handled by the IntersectionObserver in the render
    // map via {@code hydrateSession}). The original {@code drainedSessionsRef}
    // guard still prevents re-draining a terminal session on a SignalR status
    // flip; we layer the active-set / hydrated-set checks on top.
    const isInFlight = (s: SessionSummary) =>
      s.status === AgentSessionStatus.Running ||
      s.status === AgentSessionStatus.Pending ||
      s.status === AgentSessionStatus.Canceling
    const toFetch = ordered.filter((s) => {
      const isActive = activeSessionIds.has(s.id)
      const isHydrated = hydratedSessionsRef.current.has(s.id)
      // Older, never-hydrated sessions are skipped entirely here — they wait
      // for scroll-into-view hydration.
      if (!isActive && !isHydrated) return false
      const drained = drainedSessionsRef.current.has(s.id)
      // Fetch when we haven't drained this active session yet, or when it's
      // still in-flight (top-up). A drained, terminal session is a no-op.
      return !drained || isInFlight(s)
    })
    // Mark every active session as hydrated up-front — even a terminal active
    // session that's already drained (and thus not in {@code toFetch}) must
    // render fully, not as a placeholder. Bump the version so the render map
    // reflects the new hydrated set.
    let hydratedChanged = false
    for (const s of ordered) {
      if (activeSessionIds.has(s.id) && !hydratedSessionsRef.current.has(s.id)) {
        hydratedSessionsRef.current.add(s.id)
        hydratedChanged = true
      }
    }
    if (hydratedChanged) setHydratedVersion((v) => v + 1)
    if (toFetch.length === 0) return
    setHistoryLoading(true)
    ;(async () => {
      const collected = new Map<string, Message>()
      const rawCollected = new Map<string, AgentEventDto>()
      const moreFlags = new Map<string, boolean>()
      for (const session of toFetch) {
        // Revoked-before-start sessions vanish from the transcript entirely.
        // Distinguishing rule: a session that was canceled WHILE STILL PENDING
        // (i.e. {@code Status === Canceled} AND {@code startedAt} never set)
        // is a revoke — the user changed their mind, no work was done, no
        // visual residue should remain. Compare against "Stop on a running
        // turn" which yields {@code Status === Canceled} but with
        // {@code startedAt} populated; those stay in the transcript with a
        // "Canceled" status line so the user remembers what they stopped.
        //
        // The turn group for this session is implicitly skipped too: with no
        // prompt bubble AND no events (a never-started session has none), the
        // turnGroups memo creates no entry, so no orphan status line renders.
        const isRevokedBeforeStart =
          session.status === AgentSessionStatus.Canceled && !session.startedAt
        if (isRevokedBeforeStart) {
          continue
        }
        // The user's prompt is now persisted as a sequence-0 `PromptReceived`
        // event (Cursor-native schema) that is delivered LIVE via SignalR into
        // `historyById` as a hoverable, timestamped user bubble. It does NOT
        // arrive through the REST drain below: the events endpoint treats
        // `since` as EXCLUSIVE and the drain starts at `since = 0`, so it only
        // ever returns sequence ≥ 1 and never the sequence-0 prompt. The
        // synthetic bubble here is a FALLBACK for legacy sessions that have
        // `session.prompt` on the row but no `PromptReceived` event — without
        // it their prompt would vanish from the transcript. When both the
        // synthetic bubble and the live PromptReceived bubble are present, the
        // `messages` memo dedupes the synthetic one away (see the dedupe there);
        // legacy sessions with no PromptReceived keep rendering the synthetic.
        const syntheticUserId = `session:${session.id}:prompt`
        if (session.prompt) {
          collected.set(syntheticUserId, {
            kind: 'user',
            id: syntheticUserId,
            sessionId: session.id,
            sequence: 0,
            text: session.prompt,
          })
        }
        // TAIL-FIRST hydration. Two cases share this effect:
        //   (1) INITIAL hydration of an active session we've never drained →
        //       fetch the NEWEST page ({@code before: TAIL_CURSOR}, ascending)
        //       so the latest events show at the bottom in chat order. Older
        //       events load on scroll-up via {@code handleLoadEarlier}.
        //   (2) Live TOP-UP of an already-drained, still-in-flight session →
        //       page FORWARD from the highest sequence seen ({@code since},
        //       exclusive) so out-of-band progress that SignalR may have missed
        //       lands at the bottom. {@code since} is retained ONLY here, for
        //       this live gap-fill path.
        const alreadyDrained = drainedSessionsRef.current.has(session.id)
        let highest = drainedSessionsRef.current.get(session.id) ?? 0
        let lowest =
          lowestLoadedRef.current.get(session.id) ?? Number.MAX_SAFE_INTEGER
        let page: Awaited<ReturnType<typeof getApiSessionsIdEvents>>
        try {
          page = alreadyDrained
            ? await getApiSessionsIdEvents(session.id, {
                since: highest,
                limit: PAGE_LIMIT,
              })
            : await getApiSessionsIdEvents(session.id, {
                before: TAIL_CURSOR,
                limit: PAGE_LIMIT,
              })
        } catch (err) {
          // Surface the failure quietly — the transcript may be partial
          // but we still want to render what we did get.
          // eslint-disable-next-line no-console
          console.warn('[ChatCanvas] failed to load session events', session.id, err)
          continue
        }
        if (cancelled) return
        for (const raw of page ?? []) {
          const msg = eventToMessage(raw)
          if (msg) {
            collected.set(msg.id, msg)
          }
          rawCollected.set(`${raw.sessionId}:${raw.sequence}`, raw)
          highest = Math.max(highest, raw.sequence)
          lowest = Math.min(lowest, raw.sequence)
        }
        // On the INITIAL tail fetch, a full page means there are OLDER events
        // before the top of the window — offer "Load earlier". A live top-up
        // (forward) never changes the "older events" flag.
        if (!alreadyDrained) {
          moreFlags.set(session.id, (page?.length ?? 0) >= PAGE_LIMIT)
        }
        // Record the highest sequence seen (forward cursor for the next live
        // top-up) and the lowest (top-of-window cursor for "Load earlier").
        drainedSessionsRef.current.set(session.id, highest)
        if (lowest !== Number.MAX_SAFE_INTEGER) {
          lowestLoadedRef.current.set(
            session.id,
            Math.min(lowestLoadedRef.current.get(session.id) ?? lowest, lowest),
          )
        }
      }
      if (cancelled) return
      // Merge incrementally into the existing maps instead of replacing them —
      // sessions we skipped this pass keep their already-loaded events.
      if (collected.size > 0) {
        setHistoryById((prev) => {
          const next = new Map(prev)
          for (const [k, v] of collected) next.set(k, v)
          return next
        })
      }
      if (rawCollected.size > 0) {
        setRawEventsById((prev) => {
          const next = new Map(prev)
          for (const [k, v] of rawCollected) next.set(k, v)
          return next
        })
      }
      if (moreFlags.size > 0) {
        setHasMoreBySession((prev) => {
          let changed = false
          const next = new Set(prev)
          for (const [id, more] of moreFlags) {
            if (more && !next.has(id)) {
              next.add(id)
              changed = true
            } else if (!more && next.has(id)) {
              next.delete(id)
              changed = true
            }
          }
          return changed ? next : prev
        })
      }
      setHistoryLoading(false)
    })()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conversationId, sessionsSignature, activeSessionIds])

  // ── Live merge ────────────────────────────────────────────────────────────
  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onAgentEvent((evt) => {
      // Defence-in-depth filter: only merge events whose sessionId belongs
      // to one of THIS conversation's sessions. Backend now scopes the
      // broadcast to branch-{id} so sibling-branch tabs no longer see each
      // other's ticks, but multiple conversations can share one branch
      // group — without this filter, a live event for conversation B
      // would still land in conversation A's canvas if both are open in
      // the same branch. The session-id set is the cheapest correct
      // partition we have (the notification carries sessionId but not
      // conversationId consistently across legacy event types).
      //
      // Edge case: a brand-new session just created by submitPrompt won't
      // yet be in {@code sessions} because the conversation detail hasn't
      // refetched. We allow events through if their session id isn't known
      // — the conversation invalidation below picks up the new row and the
      // history reload absorbs the events on the next render. The wrong
      // pre-existing session (i.e. one from a different conversation on
      // the same branch) IS in our local universe via the previous
      // conversation query but NOT in {@code sessions}, so we'd let it
      // through here too. Strictly correct filtering requires conversation
      // id on the wire — the backend payload already carries it, so we
      // gate on that when present, and fall through to session membership
      // otherwise.
      if (
        conversationId &&
        evt.conversationId &&
        evt.conversationId !== conversationId
      ) {
        return
      }
      // Card 4 (cursor-native-chat-ux): the SignalR payload is now
      // {@code AgentEventNotification { conversationId, projectId, branchId,
      // event: AgentEventDto }}. The polymorphic union with the
      // {@code eventKind} discriminator lives on {@code evt.event} — the
      // Tapper-generated type only models the base shape, so we cast to the
      // Orval-generated union before threading it through {@code eventToMessage}.
      const wireEvent = evt.event as unknown as AgentEventDto
      const sessionIds = new Set(sessions.map((s) => s.id))
      if (
        sessionIds.size > 0 &&
        !sessionIds.has(wireEvent.sessionId) &&
        // Allow unknown sessions through ONLY when the wire payload also
        // tags them with the current conversation id — that's the brand-new
        // session case (conversation detail hasn't refetched yet).
        evt.conversationId !== conversationId
      ) {
        return
      }
      // Always stash the raw event — the {@link TurnStatusLine} reads from
      // this map to derive its label, regardless of whether the event also
      // produces a visible bubble.
      const rawId = `${wireEvent.sessionId}:${wireEvent.sequence}`
      setRawEventsById((prev) => {
        if (prev.has(rawId)) return prev
        const next = new Map(prev)
        next.set(rawId, wireEvent)
        return next
      })

      // The first event for a queued session means the daemon has dispatched
      // it — drop the queue flag so the next render falls through to the
      // normal label state machine ("Working…", "Thinking…", a tool name, …).
      setQueuedSessionIds((prev) => {
        if (!prev.has(wireEvent.sessionId)) return prev
        const next = new Set(prev)
        next.delete(wireEvent.sessionId)
        return next
      })

      const msg = eventToMessage(wireEvent)
      if (!msg) return
      setHistoryById((prev) => {
        if (prev.has(msg.id)) return prev
        const next = new Map(prev)
        next.set(msg.id, msg)
        return next
      })
      // Reconcile optimistic user bubbles: the moment the persisted
      // PromptReceived for this prompt arrives, drop the optimistic twin.
      if (msg.kind === 'user') {
        setOptimistic((prev) =>
          prev.filter((m) => m.text.trim() !== msg.text.trim()),
        )
      }
      // If a fresh session just produced an event we haven't seen in the
      // sessions list yet, nudge the conversation detail so the sidebar /
      // chrome reflect the latest activity.
      if (conversationId && !sessions.some((s) => s.id === msg.sessionId)) {
        queryClient.invalidateQueries({
          queryKey: getGetApiConversationsIdQueryKey(conversationId),
        })
      }

      // A terminal Status event (Finished / Error / Cancelled / Expired)
      // flips the session's status server-side, but we cache that status in
      // {@code conversationQuery.data.sessions} which never refetches on its
      // own. Without this invalidation the status line is stuck at "Working…"
      // until the user reloads the page — even though the assistant bubble
      // for the completed turn is already rendered. Re-fetch the conversation
      // detail so {@code SessionSummary.status} catches up to the wire event.
      if (
        conversationId &&
        isStatus(wireEvent) &&
        (wireEvent.status === 'Finished' ||
          wireEvent.status === 'Error' ||
          wireEvent.status === 'Cancelled' ||
          wireEvent.status === 'Expired')
      ) {
        queryClient.invalidateQueries({
          queryKey: getGetApiConversationsIdQueryKey(conversationId),
        })
      }
    })
    return () => {
      unsubscribe()
    }
  }, [connection, conversationId, queryClient, sessions])

  // ── Git sync banner (CommitFailed / GitPushFailed / MergeConflict) ────────
  //
  // The daemon fans out four signals as the auto-commit loop runs:
  //   - CommitMade           → the working tree just produced a commit
  //   - CommitFailed         → `git commit` exited non-zero (identity, hook,
  //                            lock, …)
  //   - GitPushFailed        → `git push` exited non-zero (auth, network,
  //                            conflict, …)
  //   - GitPushSucceeded     → the remote now has every commit we made
  //   - MergeConflict        → a merge produced conflicts (rare here, but the
  //                            same out-of-sync condition)
  //
  // The user explicitly asked for ANY of these errors to be projected back so
  // they know they're not in sync with GitHub. We do that with two surfaces:
  //   (1) a toast at the moment of failure (transient — "thing just broke");
  //   (2) a persistent banner above the composer that stays until the next
  //       GitPushSucceeded clears it (stable — "you are still out of sync").
  //
  // CommitMade *alone* is NOT enough to clear the banner — we want to see the
  // push land. That avoids a false-clear in the window where a commit lands
  // but the matching push is still retrying.
  const [gitSyncError, setGitSyncError] = useState<{
    title: string
    detail: string
    // The coarse daemon classification (e.g. 'Auth', 'Hook', 'Unknown'). When
    // 'Unknown' the title carries no actionable hint, so the banner auto-opens
    // the raw output — that tail is then the only diagnostic signal.
    reason?: string
  } | null>(null)
  useEffect(() => {
    if (!connection) return
    const unsubs: Array<() => void> = []
    unsubs.push(
      connection.onCommitFailed((p) => {
        const detail =
          p.lastOutputTail.trim().length > 0
            ? p.lastOutputTail.trim().slice(-400)
            : 'Daemon could not produce a commit.'
        const title = `Commit failed (${p.reason}) — working tree is out of sync with GitHub`
        showError(`${title}. ${commitReasonHint(p.reason)}`)
        setGitSyncError({ title, detail, reason: p.reason })
      }),
    )
    unsubs.push(
      connection.onGitPushFailed((p) => {
        const detail =
          p.lastOutputTail.trim().length > 0
            ? p.lastOutputTail.trim().slice(-400)
            : `git push exited with no output (reason=${p.reason}).`
        const title = `Push failed (${p.reason}) — local commits are not on GitHub yet`
        showError(`${title}. ${pushReasonHint(p.reason)}`)
        setGitSyncError({ title, detail, reason: p.reason })
      }),
    )
    unsubs.push(
      connection.onMergeConflict((p) => {
        const detail =
          p.summary.trim().length > 0
            ? p.summary.trim().slice(0, 400)
            : `Conflicts in ${p.files.length} file(s).`
        const title = `Merge conflict on ${p.sourceBranch} → ${p.targetBranch}`
        showError(`${title}. Resolve conflicts in the runtime, then retry.`)
        setGitSyncError({ title, detail, reason: 'Conflict' })
      }),
    )
    unsubs.push(
      connection.onGitPushSucceeded(() => {
        // Positive ack — every local commit is now on the remote. Clearing on
        // push success (not just on commit success) is intentional, see the
        // block comment above.
        setGitSyncError(null)
      }),
    )
    return () => {
      for (const u of unsubs) u()
    }
  }, [connection, showError])

  // ── Connection state (gates Send) ────────────────────────────────────────
  const [hubState, setHubState] = useState<HubConnectionState>(
    connection?.state ?? HubConnectionState.Disconnected,
  )
  useEffect(() => {
    if (!connection) {
      setHubState(HubConnectionState.Disconnected)
      return
    }
    setHubState(connection.state)
    const unsubscribe = connection.subscribeState((s) => setHubState(s))
    return () => {
      unsubscribe()
    }
  }, [connection])

  // ── Resync conversation on every Connected transition ────────────────────
  //
  // Terminal AgentEvents (TurnCompleted / TurnFailed / TurnCanceled) flip the
  // session's status server-side, and the onAgentEvent handler above
  // invalidates the conversation query when they arrive over the live wire.
  // But that fan-out is one-shot — anything pushed while the socket was down
  // (hard page reload mid-turn, brief reconnect window, unload/reconnect gap)
  // never reaches the client, and the cached SessionSummary.status stays at
  // "Running" forever. The Stop button paints from that stale status and the
  // user is stuck staring at a no-op control.
  //
  // Every time the connection enters the Connected state — both the initial
  // connect and any auto-reconnect — refetch the conversation detail so
  // {@code session.Status} re-syncs with the DB. This catches every missed
  // terminal event without polling. Once per transition-into-Connected is
  // the right cadence: hubState only re-renders when the value actually
  // changes, so the effect doesn't fire on unrelated re-renders.
  useEffect(() => {
    if (!conversationId) return
    if (hubState !== HubConnectionState.Connected) return
    queryClient.invalidateQueries({
      queryKey: getGetApiConversationsIdQueryKey(conversationId),
    })
  }, [hubState, conversationId, queryClient])

  // ── Optimistic user bubbles ──────────────────────────────────────────────
  // While a prompt is in flight (round-trip from submitPrompt → daemon
  // persists PromptReceived → push back through AgentHub) we render a local
  // bubble keyed by an "optimistic:*" id so the user never sees a dead UI.
  // Optimistic bubbles only ever represent the user's just-submitted prompt
  // — never an assistant reply, never a status row, never an auto-commit
  // trailer. Narrow to the `user` arm of the union so call sites can read
  // `.text` without a discriminator check.
  const [optimistic, setOptimistic] = useState<
    Extract<Message, { kind: 'user' }>[]
  >([])
  const [composerValue, setComposerValue] = useState('')
  const [sending, setSending] = useState(false)

  // ── Pending file attachments (chat-file-attachments) ─────────────────────
  // Each entry mounts a {@link PendingAttachmentSlot} which runs its own
  // {@link useAttachmentUpload} hook. {@code slotId} is a FRONT-END-only key —
  // the backend attachmentId arrives later via the slot calling
  // {@code onStateChange}. The aggregate {@code slotStates} record is the
  // single source of truth for Send-gating + helper text without re-rendering
  // ChatCanvas on every progress tick (a chip-local concern).
  const [pendingAttachments, setPendingAttachments] = useState<
    Array<{ slotId: string; file: File }>
  >([])
  const [slotStates, setSlotStates] = useState<
    Record<string, SlotStateSnapshot>
  >({})
  // Drag-enter/leave events fire for each nested child as the cursor moves;
  // a simple boolean would flicker. We count enter-vs-leave so the highlight
  // toggles only when the cursor actually leaves the wrapper.
  const [dragDepth, setDragDepth] = useState(0)
  // The presign endpoint requires a conversation id. ChatCanvas may not have
  // one yet (empty-state / first turn). When the user first attaches a file
  // we mint a client-side guid and thread it through the eventual
  // submitPrompt — the backend creates the conversation on demand on first
  // send when it sees an unknown id. Once minted it sticks until the route
  // swaps {@code conversationId} so all attachments in the batch share one
  // conversation. Reset whenever the prop changes so a stale minted id from
  // one conversation can't leak into another.
  const [mintedConversationId, setMintedConversationId] = useState<
    string | null
  >(null)
  useEffect(() => {
    setMintedConversationId(null)
    setPendingAttachments([])
    setSlotStates({})
    setDragDepth(0)
  }, [conversationId])
  const effectiveConversationId = conversationId ?? mintedConversationId

  // Composer input ref — lifted to the canvas so we can imperatively focus
  // the textarea when the user clicks "+ New conversation" in the picker.
  // The popover clears the ?c= search param and closes itself; the new empty
  // state mounts a fresh composer with {@code autoFocus} but MUI's Popover
  // restores focus to its trigger on close, which beats {@code autoFocus}.
  // A {@code requestAnimationFrame} delay (below) lets us win that race.
  const composerInputRef = useRef<HTMLTextAreaElement | HTMLInputElement | null>(
    null,
  )
  const prevConversationIdRef = useRef<string | null>(conversationId)
  useEffect(() => {
    const prev = prevConversationIdRef.current
    prevConversationIdRef.current = conversationId
    // Only refocus on the transitions we actually care about — namely the
    // pivot from a real conversation back to the "ready for first prompt"
    // empty state triggered by "+ New conversation". Initial mount and
    // conversation-to-conversation switches are handled by autoFocus and
    // the focusKey-driven effect inside Composer respectively.
    if (prev !== null && conversationId === null) {
      const raf = requestAnimationFrame(() => {
        // Double-RAF so we run AFTER MUI's Popover finishes restoring
        // focus to its trigger (which fires inside the unmount phase).
        requestAnimationFrame(() => {
          composerInputRef.current?.focus()
        })
      })
      return () => cancelAnimationFrame(raf)
    }
    return undefined
  }, [conversationId])

  // ── Soft-queue (P3.4) ────────────────────────────────────────────────────
  // When the backend returns {@code queued: true} from {@code SubmitPrompt}
  // we remember the session id locally. The row in the transcript flips to
  // the quieter "Waiting for the current turn to finish…" label until the
  // first event for the session arrives via the live handler — at which point
  // we drop the id and the normal state machine takes over for a smooth
  // graduation. The set is cleared whenever the active conversation changes
  // so a stale id from one conversation can't bleed into another.
  const [queuedSessionIds, setQueuedSessionIds] = useState<Set<string>>(
    () => new Set(),
  )
  useEffect(() => {
    setQueuedSessionIds(new Set())
  }, [conversationId])

  // ── Optimistic "Cancelling…" state for the activity pill ────────────────
  // Card 8 (cursor-native-chat-ux §7). When the user clicks the pill's
  // cancel control, {@link handleStop} fires the SignalR cancel call and
  // adds the session id here SYNCHRONOUSLY. {@link TurnPhaseChrome} reads
  // this set and overrides the derived pill state to {@code cancelling}
  // ("Cancelling…" + shimmer) until either:
  //   * the daemon's terminal {@code Cancelled} status event lands (the
  //     pill state machine takes over with the wire-truth), OR
  //   * the session's status flips to a terminal state via the sessions
  //     list refresh — we clear the id below.
  //
  // We also guard {@link handleStop} against double-clicks: an id already
  // in this set means the cancel has been kicked off and we no-op the
  // second invocation. Reset on conversation switch so a stale id from
  // one transcript can't bleed into another.
  const [cancellingSessionIds, setCancellingSessionIds] = useState<Set<string>>(
    () => new Set(),
  )
  useEffect(() => {
    setCancellingSessionIds(new Set())
  }, [conversationId])
  // Auto-clear the optimistic flag as soon as the wire reports the session
  // is terminal. The pill state machine then drives the "Cancelled" /
  // "Stopped" / "Timed out" presentation from the real event stream.
  useEffect(() => {
    if (cancellingSessionIds.size === 0) return
    setCancellingSessionIds((prev) => {
      let changed = false
      const next = new Set(prev)
      for (const s of sessions) {
        if (!next.has(s.id)) continue
        if (
          s.status === AgentSessionStatus.Canceled ||
          s.status === AgentSessionStatus.Succeeded ||
          s.status === AgentSessionStatus.Failed
        ) {
          next.delete(s.id)
          changed = true
        }
      }
      return changed ? next : prev
    })
  }, [sessions, cancellingSessionIds])

  // Per-phase expanded state — card 7 (cursor-native-chat-ux) moved the
  // expanded-trace toggle INSIDE {@link TurnPhaseChrome} so each phase owns
  // its own chevron + local open/closed state. The previous lift-up to
  // ChatCanvas (a {@code Set<phaseKey>}) is no longer needed; older phases
  // stay collapsed by default and re-open per-mount, which matches the
  // spec's "scrolling history reads as a quiet trail" intent.

  // The old "Show earlier" lazy-render window (a {@code showAllTurns} toggle
  // capping the initial DOM at the last N turns) was retired by
  // lazy-session-hydration: every turn group is mounted, but older ones render
  // as cheap prompt placeholders that hydrate their full transcript on
  // scroll-into-view. See {@link SessionPlaceholderTurn} and the
  // {@code hydrateSession} drain.

  // ── Retry tracking (P3.5, retired in card 7) ────────────────────────────
  // Card 7 (cursor-native-chat-ux) replaced the inline "Try again" link with
  // the Cursor-native pill + footer chrome — failed turns now surface as a
  // {@code run-error} pill state plus a "Stopped after Xs" footer line, and
  // the user re-submits from the composer rather than via a per-row retry
  // affordance. The retry handler / set state has been removed accordingly;
  // if user testing surfaces a need for an inline retry, wire it through
  // a follow-up that talks to the new pill/footer chrome instead of the
  // old TurnStatusLine.

  // ── Inline runtime spec proposal cards ──────────────────────────────────
  //
  // When the agent submits a runtime spec proposal via the
  // `propose_runtime_spec` MCP tool, the backend writes a row with
  // Status = Pending and fans out RuntimeProposalCreated to the
  // `project-{projectId}` SignalR group. The super-admin runtime workspace
  // page renders these — but the chat workspace is where the user actually
  // lives, so we surface the same card here so the approve / edit / reject
  // affordances are one inline glance away from the agent message that
  // asked for them.
  //
  // The fetch is the source of truth; the SignalR hook just invalidates the
  // cached list (and downstream proposal-id / runtime-spec / status keys)
  // so the UI re-renders without polling. Same pattern the super-admin
  // page uses — code path is shared via {@code useProposalSignalR}.
  useProposalSignalR(connection, projectId)
  const pendingProposalsQuery = useGetApiProjectsProjectIdProposals(
    projectId,
    { status: RuntimeProposalStatus.Pending },
    { query: { enabled: !!projectId } },
  )
  const pendingProposals: RuntimeProposalDto[] = useMemo(
    () => pendingProposalsQuery.data ?? [],
    [pendingProposalsQuery.data],
  )

  // ── Revoke a queued (Pending) user message ──────────────────────────────
  // Clicking the revoke affordance on a queued user bubble:
  //   1. Pours the message text back into the composer so the user can
  //      tweak and resend.
  //   2. Asks the hub to cancel the still-Pending session. The backend's
  //      CancelSessionCommand drops Pending sessions straight to terminal
  //      Canceled and raises {@code AgentSessionTerminated}, which lets the
  //      DispatchNextSessionHandler advance the runtime's queue.
  //   3. Optimistically removes the session from the local queued set so
  //      the bubble's "Queued" chip disappears immediately; the conversation
  //      detail invalidation below will replace the synthesised user bubble
  //      with the persisted Canceled session, or strip it once the next
  //      send takes its place.
  //
  // We don't roll the optimistic state back on a wire error — the user has
  // already seen their text in the composer, and rolling back would be more
  // confusing than the failed cancel itself. The error toast surfaces the
  // failure for the rare case the cancel didn't land.
  const handleRevoke = useCallback(
    async (sessionId: string, text: string) => {
      if (!connection) return
      // 1. Pour text back into the composer. If the composer already has
      //    content (the user was typing a follow-up), prepend with a separator
      //    so we don't silently destroy their in-flight draft.
      setComposerValue((current) => {
        const trimmedCurrent = current.trim()
        if (trimmedCurrent.length === 0) return text
        return `${text}\n\n${trimmedCurrent}`
      })
      // 2. Optimistically clear the queued chip so the UI reads as "revoking".
      setQueuedSessionIds((prev) => {
        if (!prev.has(sessionId)) return prev
        const next = new Set(prev)
        next.delete(sessionId)
        return next
      })
      // 3. Fire the cancel through the hub — server will drop the Pending
      //    session and advance the queue.
      try {
        await connection.cancelTurn({ sessionId })
        // Refresh conversation detail so the now-Canceled session row
        // reflects its terminal state in the transcript.
        if (conversationId) {
          queryClient.invalidateQueries({
            queryKey: getGetApiConversationsIdQueryKey(conversationId),
          })
        }
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[ChatCanvas] revoke (cancelTurn) failed', err)
        showError(extractSubmitErrorMessage(err))
      }
    },
    [connection, conversationId, queryClient, showError],
  )

  // ── Stop an in-flight turn ──────────────────────────────────────────────
  // The stop button (rendered next to the composer's send affordance when a
  // session is Running / Booting) routes through the same hub method as
  // revoke: CancelSessionCommand handles Running → Canceling and pushes the
  // cancel down to the daemon, which closes the loop with a terminal
  // {@code turn_canceled} event that flips Canceling → Canceled.
  const handleStop = useCallback(
    async (sessionId: string) => {
      if (!connection) return
      // Double-click guard — once an optimistic cancel is in flight for this
      // session, drop subsequent clicks. The hub call itself is idempotent
      // (CancelSessionCommand collapses to a no-op on already-Canceling
      // sessions), but skipping the network round-trip keeps the audit log
      // clean and prevents the rare race where a second click could re-arm
      // the optimistic flag after the wire-truth had already cleared it.
      if (cancellingSessionIds.has(sessionId)) return
      // Card 8: flip the pill to "Cancelling…" synchronously so the user
      // sees their click register before the SignalR roundtrip finishes.
      // The terminal {@code Cancelled} status event arriving via SignalR
      // (or the sessions-list refetch landing terminal status) clears the
      // flag automatically via the effect above.
      setCancellingSessionIds((prev) => {
        if (prev.has(sessionId)) return prev
        const next = new Set(prev)
        next.add(sessionId)
        return next
      })
      try {
        await connection.cancelTurn({ sessionId })
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[ChatCanvas] stop (cancelTurn) failed', err)
        showError(extractSubmitErrorMessage(err))
        // Roll back the optimistic flag on a hub-level failure (auth,
        // connection drop, etc.) so the pill returns to its pre-click state
        // instead of being stuck on "Cancelling…" with no in-flight cancel.
        // We DON'T roll back on a successful invocation that the daemon
        // later fails to honour — the orphan-session janitor is the safety
        // net there, and the pill should keep saying "Cancelling…" until
        // the session lands terminal.
        setCancellingSessionIds((prev) => {
          if (!prev.has(sessionId)) return prev
          const next = new Set(prev)
          next.delete(sessionId)
          return next
        })
      }
    },
    [connection, showError, cancellingSessionIds],
  )

  // ── Ordered message list ─────────────────────────────────────────────────
  // Sort by (sessionCreatedAt, sequence). For sessions we haven't loaded yet
  // (live event from a brand-new session before its row lands in the sessions
  // list) we synthesise a far-future sort key so they appear last — which is
  // the correct ordering anyway.
  //
  // Status-kind messages are filtered out — P3.2 renders one
  // {@link TurnStatusLine} per turn instead of per-event status placeholders.
  const messages = useMemo<Exclude<Message, { kind: 'status' }>[]>(() => {
    const sessionOrder = new Map<string, string>()
    for (const s of sessions) sessionOrder.set(s.id, s.createdAt)
    const arr = Array.from(historyById.values()).filter(
      (m): m is Exclude<Message, { kind: 'status' }> => m.kind !== 'status',
    )
    arr.sort((a, b) => {
      const aCreated = sessionOrder.get(a.sessionId) ?? '\uffff'
      const bCreated = sessionOrder.get(b.sessionId) ?? '\uffff'
      if (aCreated !== bCreated) return aCreated.localeCompare(bCreated)
      return a.sequence - b.sequence
    })
    // Dedupe the synthetic prompt fallback against the real PromptReceived
    // bubble. The synthetic bubble (`session:<id>:prompt`, no createdAt → not
    // hoverable) is hydrated from `session.prompt`, while the live SignalR
    // merge delivers the sequence-0 PromptReceived as `<sessionId>:0` (with a
    // timestamp). Both can coexist in `historyById` under different keys, so
    // when a session has the real persisted user bubble we drop its synthetic
    // counterpart — independent of arrival order. Legacy sessions that have
    // only the synthetic fallback never enter `realUserSessions`, so they're
    // left untouched.
    const realUserSessions = new Set<string>()
    for (const m of arr) {
      if (m.kind === 'user' && !(m.id.startsWith('session:') && m.id.endsWith(':prompt'))) {
        realUserSessions.add(m.sessionId)
      }
    }
    const deduped = arr.filter(
      (m) =>
        !(
          m.kind === 'user' &&
          m.id.startsWith('session:') &&
          m.id.endsWith(':prompt') &&
          realUserSessions.has(m.sessionId)
        ),
    )
    // Optimistic bubbles tail the persisted ones — they're always "newest".
    // Optimistic state is already narrowed to the `user` arm of the union at
    // its `useState` declaration, so no further filtering is needed.
    return [...deduped, ...optimistic]
  }, [historyById, optimistic, sessions])

  // ── Events grouped by session, sorted by sequence ───────────────────────
  // Drives the label state machine in each {@link TurnStatusLine}.
  const eventsBySession = useMemo<Map<string, AgentEventDto[]>>(() => {
    const map = new Map<string, AgentEventDto[]>()
    for (const evt of rawEventsById.values()) {
      const bucket = map.get(evt.sessionId)
      if (bucket) {
        bucket.push(evt)
      } else {
        map.set(evt.sessionId, [evt])
      }
    }
    for (const bucket of map.values()) {
      bucket.sort((a, b) => a.sequence - b.sequence)
    }
    return map
  }, [rawEventsById])

  // ── Turn-grouped render plan ────────────────────────────────────────────
  // The transcript is rendered turn-by-turn so the {@link TurnStatusLine}
  // lands between the user bubble and the assistant bubbles of the same
  // session. Sessions are sorted by createdAt; bubbles within a session by
  // sequence. Optimistic bubbles tail the persisted turns and have no
  // status line attached.
  interface TurnGroup {
    sessionId: string
    /** {@code null} for optimistic-only "pseudo turns" with no session yet. */
    session: SessionSummary | null
    userBubbles: Extract<Message, { kind: 'user' }>[]
    assistantBubbles: Extract<Message, { kind: 'assistant' }>[]
    // Lazy-session-hydration: false for an older session that hasn't been
    // drained yet — its turn group renders as a lightweight prompt placeholder
    // (showing {@code session.prompt}) instead of mounting its full event list,
    // and an IntersectionObserver triggers {@link hydrateSession} when it
    // scrolls into view. Active / optimistic groups are always hydrated.
    isHydrated: boolean
    // Card 4 (cursor-native-chat-ux): the auto-commit trailer field is gone —
    // the new wire union has no {@code CommitMade} event. Per-turn git metadata
    // (branch / PR url) now rides on the {@link RunResultDto} surfaced by card
    // 7; until that lands the trailer is intentionally absent.
  }

  const turnGroups = useMemo<TurnGroup[]>(() => {
    // {@code hydratedVersion} is read so this memo recomputes when the hydrated
    // set mutates (the ref itself isn't a reactive dependency).
    void hydratedVersion
    const groups = new Map<string, TurnGroup>()
    const sessionById = new Map<string, SessionSummary>()
    for (const s of sessions) sessionById.set(s.id, s)

    const ensure = (sessionId: string): TurnGroup => {
      let g = groups.get(sessionId)
      if (!g) {
        const session = sessionById.get(sessionId) ?? null
        // A group is "hydrated" when it has no session (optimistic just-sent),
        // is in the active set (eager-rendered live turn), or its id is in the
        // sticky hydrated set (it was drained on scroll). Everything else is a
        // placeholder.
        const isHydrated =
          session === null ||
          activeSessionIds.has(sessionId) ||
          hydratedSessionsRef.current.has(sessionId)
        g = {
          sessionId,
          session,
          userBubbles: [],
          assistantBubbles: [],
          isHydrated,
        }
        groups.set(sessionId, g)
      }
      return g
    }

    for (const m of messages) {
      const g = ensure(m.sessionId)
      if (m.kind === 'user') {
        g.userBubbles.push(m)
      } else if (m.kind === 'assistant') {
        g.assistantBubbles.push(m)
      }
      // 'status' / 'thinking' / 'tool' / 'task' messages aren't grouped here —
      // TurnStatusLine + TurnTrace render from the raw event stream directly
      // via {@code phase.events}.
    }

    // Also ensure a group exists for every session that has raw events
    // (even if no visible bubble was produced) so its status line still
    // renders — e.g. a session with only TurnStarted + TurnCompleted shows
    // "Done" in the transcript.
    for (const sessionId of eventsBySession.keys()) ensure(sessionId)

    // Lazy-session-hydration: ensure a group exists for EVERY persisted session
    // too, so older un-drained sessions render as prompt placeholders instead
    // of vanishing from the timeline. Revoked-before-start sessions (canceled
    // while still Pending, never started) carry no transcript surface and are
    // intentionally excluded so no orphan placeholder appears.
    for (const s of sessions) {
      const isRevokedBeforeStart =
        s.status === AgentSessionStatus.Canceled && !s.startedAt
      if (isRevokedBeforeStart) continue
      ensure(s.id)
    }

    const ordered = Array.from(groups.values())
    ordered.sort((a, b) => {
      const aCreated = a.session?.createdAt ?? '\uffff'
      const bCreated = b.session?.createdAt ?? '\uffff'
      return aCreated.localeCompare(bCreated)
    })
    // Within each group: user bubbles by sequence, assistant bubbles by sequence.
    for (const g of ordered) {
      g.userBubbles.sort((a, b) => a.sequence - b.sequence)
      g.assistantBubbles.sort((a, b) => a.sequence - b.sequence)
    }
    return ordered
  }, [messages, sessions, eventsBySession, activeSessionIds, hydratedVersion])

  // ── Render window (superseded by lazy-session-hydration) ─────────────────
  //
  // The old "Show earlier" window capped the initial DOM at the last
  // {@code INITIAL_TURN_WINDOW} turns because every mounted turn re-parsed
  // markdown and mounted a status line + trace. Lazy-session-hydration
  // replaces that: ALL turn groups render now, but older ones render as
  // lightweight prompt placeholders (no event list, no markdown) until they
  // scroll into view. So we render the full {@code turnGroups} list — the
  // placeholders keep the DOM cheap and, crucially, must be mounted so each
  // one's IntersectionObserver can fire and hydrate on scroll.
  const visibleTurnGroups = turnGroups

  // ── Scroll behavior ──────────────────────────────────────────────────────
  //
  // Industry-standard "sticky-when-at-bottom" pattern — the same approach
  // used by ChatGPT, Claude.ai, Perplexity, Slack, Discord, and Vercel AI
  // Elements. While the viewport is within ~one line of body text from the
  // bottom we auto-snap on every content growth (ResizeObserver-driven);
  // the moment the user wheels / touches / page-keys away, the stick
  // releases and the "Jump to latest" Fab appears.
  //
  // See {@link useStickToBottom} for the algorithm — it owns the refs,
  // observer, listeners, and the {@code isAtBottom} state.
  //
  // NOTE: the hook is now declared earlier (above the hydration handlers) so
  // {@code handleLoadEarlier} can read {@code scrollRef.current} for scroll-
  // position preservation when prepending older pages.

  // ── Live-turn flag (drives the static tail spacer) ─────────────────────
  //
  // True iff the latest visible turn group is still in flight: no session
  // yet (= optimistic, just sent) OR session status is Running / Pending /
  // Canceling. Drives the spacer's height in the JSX below — present
  // (clamp(220px, 45vh, 440px)) while live so the on-send "scroll user
  // bubble to top" has somewhere to scroll TO, collapsed (0) when dormant.
  const isLatestTurnLive = useMemo(() => {
    if (visibleTurnGroups.length === 0) return false
    const last = visibleTurnGroups[visibleTurnGroups.length - 1]
    const status = last.session?.status
    // Only an optimistic-just-submitted group (no session row yet, carrying
    // an "optimistic:*" sessionId) counts as live without a session. A
    // historical group whose session row simply isn't in the payload (real
    // sessionId, null session) is NOT live — mirrors the phaseIsLive gate so
    // the tail spacer doesn't linger on a finished turn.
    if (!last.session) return last.sessionId.startsWith('optimistic:')
    return (
      status === AgentSessionStatus.Running ||
      status === AgentSessionStatus.Pending ||
      status === AgentSessionStatus.Canceling
    )
  }, [visibleTurnGroups])

  // ── Live-turn assistant content height (drives the dynamic tail spacer) ──
  //
  // The tail spacer reserves room below the live turn so the on-send
  // "scroll user bubble to top" has somewhere to scroll TO. If the spacer
  // stayed a constant clamp(220px, 45vh, 440px) for the WHOLE response, the
  // stick-to-bottom hook would pin the BOTTOM OF THE SPACER (part of
  // scrollHeight) to the viewport bottom, leaving the assistant message
  // floating ~45vh above the real bottom for the entire stream.
  //
  // Fix (ChatGPT / Cursor model): the reserved room is CONSUMED by the
  // streaming content. We measure the rendered height of the live turn's
  // assistant content (everything below the user bubble — the phases area)
  // and set spacer = max(0, reservedRoom − measuredHeight). At send the
  // assistant content is ~0 → spacer ≈ reservedRoom (bubble reaches top);
  // as the response grows the spacer shrinks 1:1 so the message fills
  // downward to the real bottom; once the content exceeds reservedRoom the
  // spacer collapses to 0 and the transcript scrolls normally.
  const [liveContentHeightPx, setLiveContentHeightPx] = useState(0)
  const liveContentObserverRef = useRef<ResizeObserver | null>(null)
  // Callback ref attached to the live turn's assistant-content wrapper. It
  // (re)wires a ResizeObserver onto whichever node is the current live
  // turn's content, and tears the previous one down. When the node detaches
  // (turn finishes / new turn mounts) the callback fires with null and we
  // reset the measured height so the next live turn starts from a full
  // reserved room.
  const liveContentRef = useCallback((node: HTMLDivElement | null) => {
    liveContentObserverRef.current?.disconnect()
    liveContentObserverRef.current = null
    if (!node) {
      setLiveContentHeightPx(0)
      return
    }
    setLiveContentHeightPx(node.offsetHeight)
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (!entry) return
      // borderBoxSize is the most reliable cross-browser measure; fall back
      // to the target's offsetHeight when the API shape isn't present.
      const next =
        entry.borderBoxSize?.[0]?.blockSize ??
        (entry.target as HTMLElement).offsetHeight
      setLiveContentHeightPx(next)
    })
    observer.observe(node)
    liveContentObserverRef.current = observer
  }, [])

  // Defensive cleanup on unmount (the callback ref handles the live cases).
  useEffect(() => {
    return () => {
      liveContentObserverRef.current?.disconnect()
      liveContentObserverRef.current = null
    }
  }, [])

  // ── Composer / submit ────────────────────────────────────────────────────

  // Soft-queue (P3.4): the composer stays enabled even while a turn is
  // in-flight — the backend parks subsequent prompts behind the running turn
  // and the transcript row renders a quieter "Waiting…" line. We do still
  // gate on {@code sending} (the synchronous in-flight {@code SubmitPrompt})
  // so a double-click on Send can't fire two RPCs.
  const latestSession = useMemo(() => {
    if (sessions.length === 0) return null
    return [...sessions].sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0]
  }, [sessions])

  // ── Branch divergence banner gate ────────────────────────────────────────
  // When the daemon's first-session FF pull fails because origin has diverged
  // from the volume, the backend lands the session as Failed with a
  // {@code failureReason} of {@code 'branch_divergent'} (see the
  // daemon-git-sync-redesign spec, Scene 5). The banner is read-only in v1 —
  // the user resolves divergence in their own tooling and starts a new
  // session. The check is intentionally string-equality on the latest
  // session's failureReason so the banner clears the moment a follow-up
  // session succeeds. Forward-compatible: until the backend emits this
  // reason, the gate is always false.
  const showBranchDivergentBanner =
    latestSession?.failureReason === 'branch_divergent'

  // ── Stop affordance state ────────────────────────────────────────────────
  // The Stop button on the composer is visible whenever the latest session
  // is in flight ({@code Running} or {@code Canceling}). Pending sessions
  // are addressed via the Revoke affordance on the queued bubble — surfacing
  // both at once would be noisy. Terminal statuses hide Stop entirely.
  //
  // Note: there is no client-side {@code Booting} session status (that's the
  // runtime state, not the session status). Sessions move Pending → Running
  // when the daemon dispatches them, so {@code Running} is the right gate
  // for "an AI turn is happening right now."
  const stopVisibleStatus =
    latestSession?.status === AgentSessionStatus.Running ||
    latestSession?.status === AgentSessionStatus.Canceling
  const stopOnClick =
    stopVisibleStatus && latestSession
      ? () => void handleStop(latestSession.id)
      : undefined
  const stoppingForButton =
    latestSession?.status === AgentSessionStatus.Canceling

  const isConnected = hubState === HubConnectionState.Connected && !!connection

  // ── Attachment plumbing (chat-file-attachments) ──────────────────────────
  // The presign endpoint requires a conversation id. When the user first
  // attaches a file we mint a client-side guid (if none exists yet) so every
  // slot in the batch presigns against the same conversation — the backend
  // creates it on demand on first send.
  const ensureConversationId = useCallback((): string => {
    if (conversationId) return conversationId
    if (mintedConversationId) return mintedConversationId
    const fresh = crypto.randomUUID()
    setMintedConversationId(fresh)
    return fresh
  }, [conversationId, mintedConversationId])

  // Slot → parent state lift. Called on every (state, attachmentId)
  // transition. We bail out of the set when nothing parent-visible changed so
  // a chip's progress reducer ticking doesn't re-render the whole canvas.
  const handleSlotStateChange = useCallback(
    (slotId: string, snapshot: SlotStateSnapshot) => {
      setSlotStates((prev) => {
        const existing = prev[slotId]
        if (
          existing &&
          existing.state === snapshot.state &&
          existing.attachmentId === snapshot.attachmentId
        ) {
          return prev
        }
        return { ...prev, [slotId]: snapshot }
      })
    },
    [],
  )

  const handleRemoveAttachment = useCallback((slotId: string) => {
    setPendingAttachments((prev) => prev.filter((p) => p.slotId !== slotId))
    setSlotStates((prev) => {
      if (!(slotId in prev)) return prev
      const { [slotId]: _gone, ...rest } = prev
      void _gone
      return rest
    })
  }, [])

  // Common handler for both drag-drop and the paperclip picker. Mints a
  // conversation id (if absent) BEFORE creating the slots so every slot is
  // wired to the same conversation.
  const handleFilesPicked = useCallback(
    (fileList: FileList | File[] | null) => {
      if (!fileList) return
      const files = Array.from(fileList)
      if (files.length === 0) return
      ensureConversationId()
      setPendingAttachments((prev) => [
        ...prev,
        ...files.map((file) => ({ slotId: crypto.randomUUID(), file })),
      ])
    },
    [ensureConversationId],
  )

  // Dragged text / browser-internal drags must NOT light up the zone — gate
  // on the dataTransfer carrying actual files.
  const handleDragEnter = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer.types.includes('Files')) return
    e.preventDefault()
    setDragDepth((d) => d + 1)
  }, [])
  const handleDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer.types.includes('Files')) return
    // Required for the subsequent drop event to fire.
    e.preventDefault()
  }, [])
  const handleDragLeave = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (!e.dataTransfer.types.includes('Files')) return
    setDragDepth((d) => Math.max(0, d - 1))
  }, [])
  const handleDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      if (!e.dataTransfer.types.includes('Files')) return
      e.preventDefault()
      setDragDepth(0)
      handleFilesPicked(e.dataTransfer.files)
    },
    [handleFilesPicked],
  )

  // Derived attachment summary — single pass so every consumer (Send disabled,
  // helper line, clear-on-send) reads from one source of truth.
  const attachmentSummary = useMemo(() => {
    const snapshots = Object.values(slotStates)
    let uploading = 0
    let staging = 0
    let inFlight = 0
    for (const s of snapshots) {
      if (s.state === 'uploading') uploading += 1
      if (s.state === 'staging') staging += 1
      // 'rejected' / 'uploadFailed' / 'stagingFailed' are user-visible errors
      // the user must resolve (Retry or Remove) but they don't BLOCK send —
      // they just won't be delivered. 'ready' is terminal-good. Everything
      // else ('queued' / 'uploading' / 'staging') keeps send gated.
      if (
        s.state !== 'ready' &&
        s.state !== 'rejected' &&
        s.state !== 'uploadFailed' &&
        s.state !== 'stagingFailed'
      ) {
        inFlight += 1
      }
    }
    return { uploading, staging, inFlight }
  }, [slotStates])

  // Helper-text below the input. Null when nothing useful to say (so the row
  // collapses instead of reserving empty vertical space).
  const attachmentHelperText = useMemo<string | null>(() => {
    if (attachmentSummary.uploading > 0) {
      return attachmentSummary.uploading === 1
        ? 'Waiting for attachments to finish uploading…'
        : `Uploading ${attachmentSummary.uploading} attachments…`
    }
    if (attachmentSummary.staging > 0) {
      return 'Preparing attachments for the agent…'
    }
    return null
  }, [attachmentSummary])

  // P4.3: Failed / Crashed is the ONE state where the composer locks — the
  // runtime physically cannot accept the prompt until the user recreates it.
  // All other non-Online states (Booting / Suspended / …) keep the composer
  // open: the backend now queues prompts during Booting (P1.6) and a Suspended
  // runtime auto-wakes on send (P4.2).
  const isRuntimeUnsendable =
    runtimeState === RuntimeState.Failed ||
    runtimeState === RuntimeState.Crashed
  const canSubmit =
    isConnected &&
    !sending &&
    !isRuntimeUnsendable &&
    composerValue.trim().length > 0 &&
    attachmentSummary.inFlight === 0

  const sendDisabledReason: string | null = !isConnected
    ? 'Connecting to runtime…'
    : isRuntimeUnsendable
      ? 'Runtime needs recreating before you can send.'
      : attachmentSummary.inFlight > 0
        ? attachmentSummary.staging > 0
          ? 'Preparing attachments for the agent…'
          : 'Waiting for attachments to finish uploading…'
        : null

  const handleSubmit = useCallback(async () => {
    const text = composerValue.trim()
    if (!text || !connection || sending || !isConnected) return
    // Block at the boundary too, in case canSubmit was stale (e.g. Enter
    // pressed in the same frame an upload transitioned out of 'ready').
    if (attachmentSummary.inFlight > 0) return
    setSending(true)
    // Append an optimistic user bubble — reconciled when PromptReceived lands.
    const optimisticId = `optimistic:${Date.now()}`
    setOptimistic((prev) => [
      ...prev,
      {
        kind: 'user',
        id: optimisticId,
        // The optimistic bubble has no real session yet — these placeholder
        // values are never compared against persisted events.
        sessionId: optimisticId,
        sequence: 0,
        text,
        optimistic: true,
      },
    ])
    setComposerValue('')
    // "Give room" on send: smooth-scroll the new user bubble to the TOP of
    // the viewport so the empty canvas below (= the tail spacer) becomes
    // the reading focus for the response that's about to stream in.
    //
    // Why this works:
    //   * The optimistic bubble's id (`optimistic:${ts}`) is on its outer
    //     Box as `data-message-id`, so we can target it after rAF flushes
    //     React's commit.
    //   * The tail spacer is rendered below the latest (= live) turn — it
    //     just appeared because this fresh turn IS the latest live one —
    //     so {@code scrollHeight} grew by ~half a viewport and there's
    //     actually room to scroll the bubble all the way to the top.
    //   * {@code scrollMarginTop: 24px} (set on the MessageBubble outer
    //     Box) means scrollIntoView leaves a 24px cushion above the bubble
    //     rather than jamming it into the top edge.
    //
    // Note: the smooth scroll briefly leaves us "not at bottom" from the
    // hook's perspective; that's fine — the user is about to read the
    // response from the top of the viewport. If they want to follow the
    // tail later they can click "Jump to latest."
    requestAnimationFrame(() => {
      const el = scrollRef.current
      // CSS escape the id so the colon in `optimistic:1234567890` is
      // a valid attribute selector. CSS.escape is widely supported
      // (Safari 10+, Chrome 53+, Firefox 31+).
      const target = el?.querySelector<HTMLElement>(
        `[data-message-id="${typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(optimisticId) : optimisticId}"]`,
      )
      if (target) {
        target.scrollIntoView({ behavior: 'smooth', block: 'start' })
      } else {
        // Fallback if the bubble isn't in the DOM yet (race with React) —
        // jump to the bottom so the user still sees their message.
        scrollToBottom('auto')
      }
    })
    try {
      // Per-conversation model override is sticky in localStorage (see
      // {@link useAgentModelOverride}). Read it at submit time so the choice
      // flips for the NEXT turn even if the user switched models mid-stream.
      const overrideForSend = readAgentModelOverride(conversationId)
      // If the user attached files in the empty state we minted a conversation
      // id client-side; forward that exact id so the backend stamps the draft
      // attachments (UploadedAt!=null && SessionId==null) onto this turn and
      // creates the conversation under it instead of generating its own.
      const conversationIdForSubmit = conversationId ?? mintedConversationId
      const res = await connection.submitPrompt({
        projectId,
        branchId,
        text,
        ...(conversationIdForSubmit
          ? { conversationId: conversationIdForSubmit }
          : {}),
        ...(overrideForSend ? { agentModelId: overrideForSend } : {}),
        yolo: true,
      })
      // Soft-queue (P3.4): if the backend parked this prompt behind an
      // in-flight turn for the same project+branch, remember the session id
      // so the transcript row renders the quieter "Waiting…" rhythm until
      // the daemon dispatches it (first agent event arrives).
      if (res?.queued && res.sessionId) {
        setQueuedSessionIds((prev) => {
          const next = new Set(prev)
          next.add(res.sessionId)
          return next
        })
      }
      // First send from the empty state — pivot the URL to the new
      // conversation id so chrome + sidebar follow without unmounting.
      if (!conversationId && res?.conversationId) {
        const nextParams = new URLSearchParams(searchParams)
        nextParams.set('c', res.conversationId)
        setSearchParams(nextParams, { replace: true })
      }
      // Refresh the conversation detail (sessions) and the sidebar list so
      // the new session row shows up immediately.
      const targetId = res?.conversationId ?? conversationId
      if (targetId) {
        queryClient.invalidateQueries({
          queryKey: getGetApiConversationsIdQueryKey(targetId),
        })
      }
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdConversationsQueryKey(projectId),
      })
      // Successful submit — drop the attachment chips. Slot unmount cleans up
      // each chip's XHR / SignalR subscription. The backend auto-associates
      // the draft attachments with this turn server-side, so there's nothing
      // more for the client to send. We DON'T clear on failure so the user can
      // retry-send without re-uploading.
      setPendingAttachments([])
      setSlotStates({})
    } catch (err) {
      // Roll the optimistic bubble back, surface the server's message to the
      // user via the global snackbar (so they actually see *why* it failed —
      // e.g. missing Anthropic credentials on the project), and put the
      // failed text back into the composer so it doesn't vanish into the
      // void on a retry.
      // eslint-disable-next-line no-console
      console.warn('[ChatCanvas] submitPrompt failed', err)
      setOptimistic((prev) => prev.filter((m) => m.id !== optimisticId))
      setComposerValue((current) => (current.trim().length === 0 ? text : current))
      showError(extractSubmitErrorMessage(err))
    } finally {
      setSending(false)
    }
  }, [
    composerValue,
    connection,
    sending,
    isConnected,
    projectId,
    branchId,
    conversationId,
    mintedConversationId,
    attachmentSummary,
    searchParams,
    setSearchParams,
    queryClient,
    showError,
    scrollToBottom,
  ])

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      void handleSubmit()
    }
  }

  // P4.2 + P4.3: When the runtime is in a non-Online state the composer
  // placeholder choreographs what sending will do. The empty-state composer
  // (Scenario 1) uses {@code autoFocus}; the transcript composer gets focused
  // on conversation switch via the {@code TextField inputRef} below.
  //
  // The placeholder hierarchy:
  //   1. Not connected → "Connecting…"
  //   2. Runtime Failed / Crashed → "Recreate the runtime to send messages."
  //      (The composer is also disabled in this state.)
  //   3. Runtime Suspended / Suspending → "Send a message — we'll wake the runtime…"
  //   4. Runtime Pending / Booting / Bootstrapping / Waking → warming copy
  //      that hints the prompt is queued until the runtime is ready.
  //   5. Default → "Send a message…"
  const isSuspendedLike =
    runtimeState === RuntimeState.Suspended ||
    runtimeState === RuntimeState.Suspending
  const isBootingLike =
    runtimeState === RuntimeState.Pending ||
    runtimeState === RuntimeState.Booting ||
    runtimeState === RuntimeState.Bootstrapping ||
    runtimeState === RuntimeState.Waking
  const composerPlaceholder = !isConnected
    ? 'Connecting…'
    : isRuntimeUnsendable
      ? 'Recreate the runtime to send messages.'
      : isSuspendedLike
        ? "Send a message — we'll wake the runtime…"
        : isBootingLike
          ? 'Runtime is warming up — type to queue your first message…'
          : 'Send a message…'

  // ── Render ────────────────────────────────────────────────────────────────

  const isEmptyState = !conversationId
  // Stale-conversation 404: ?c={id} points at a conversation that was
  // archived/deleted elsewhere (another tab, or by the time we got here).
  // Show a one-line "no longer available" message + a button to drop ?c=
  // and pivot to the fresh empty state on the current branch.
  const conversationError = conversationQuery.error
  const isStaleConversation =
    !!conversationId &&
    conversationError instanceof AxiosError &&
    conversationError.response?.status === 404
  const clearStaleConversationParam = useCallback(() => {
    clearLastBranchConversationId(branchId)
    const nextParams = new URLSearchParams(searchParams)
    nextParams.delete('c')
    setSearchParams(nextParams, { replace: true })
  }, [branchId, searchParams, setSearchParams])
  void slug // currently unused but kept for parity with sibling components.

  // Bundle of attachment wiring threaded down to the Composer (both the
  // empty-state and transcript variants). All-or-nothing: the Composer only
  // renders the attach affordance + pending strip when this is supplied with
  // a usable conversation id. {@code effectiveConversationId} may still be
  // null until the user actually attaches a file (which mints one), so the
  // Composer guards each pending slot on {@code conversationId} being present.
  const composerAttachments: ComposerAttachmentsProps = {
    conversationId: effectiveConversationId,
    branchId,
    pending: pendingAttachments,
    onSlotStateChange: handleSlotStateChange,
    onRemove: handleRemoveAttachment,
    helperText: attachmentHelperText,
    isDragOver: dragDepth > 0,
    onFilesPicked: handleFilesPicked,
    onDragEnter: handleDragEnter,
    onDragOver: handleDragOver,
    onDragLeave: handleDragLeave,
    onDrop: handleDrop,
  }

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: 'instrument.chrome',
      }}
    >
      {isStaleConversation ? (
        <StaleConversationBody onStartNew={clearStaleConversationParam} />
      ) : isEmptyState ? (
        <EmptyStateBody
          projectName={projectName}
          branchName={branchName}
          composerValue={composerValue}
          onChange={setComposerValue}
          onKeyDown={handleKeyDown}
          onSubmit={() => void handleSubmit()}
          canSubmit={canSubmit}
          sending={sending}
          isConnected={isConnected}
          sendDisabledReason={sendDisabledReason}
          placeholder={composerPlaceholder}
          projectId={projectId}
          conversationId={conversationId ?? null}
          composerInputRef={composerInputRef}
          onComposerFocusChange={onComposerFocusChange}
          attachments={composerAttachments}
        />
      ) : (
        <>
          {/* Transcript — wrapped in a relative box so the jump-to-bottom
              pill can sit absolutely positioned inside the scroll viewport
              without interfering with the scroll math. */}
          <Box
            sx={{
              flex: 1,
              minHeight: 0,
              position: 'relative',
              display: 'flex',
              flexDirection: 'column',
            }}
          >
          <Box
            ref={scrollRef}
            sx={{
              flex: 1,
              minHeight: 0,
              overflowY: 'auto',
              px: { xs: 2, md: 4 },
              py: { xs: 2, md: 3 },
            }}
          >
            <Box ref={contentRef} sx={{ width: '100%', maxWidth: '100%' }}>
              {historyLoading && messages.length === 0 ? (
                <Stack alignItems="center" sx={{ py: 6 }}>
                  <CircularProgress
                    size={20}
                    thickness={4}
                    sx={{ color: tokens.textFaint }}
                  />
                </Stack>
              ) : (
                <Stack spacing={1.25}>
                  {/* Lazy-session-hydration superseded the old "Show earlier"
                      window: every turn group is mounted now, with older ones
                      rendered as cheap prompt placeholders that hydrate on
                      scroll-into-view (see SessionPlaceholderTurn below). */}
                  {visibleTurnGroups.map((group, groupIdx) => {
                    // Lazy-session-hydration: an older session that hasn't been
                    // drained renders as a lightweight prompt placeholder. The
                    // placeholder's IntersectionObserver triggers its single-
                    // session drain ({@link hydrateSession}) on scroll-into-view;
                    // once hydrated the group flips to the full render below.
                    // We never mount its event list / phases / PhaseChrome here,
                    // so run-result is NOT fanned out for placeholders.
                    if (!group.isHydrated) {
                      return (
                        <SessionPlaceholderTurn
                          key={group.sessionId}
                          sessionId={group.sessionId}
                          prompt={group.session?.prompt}
                          onHydrate={handleHydrateSession}
                        />
                      )
                    }
                    const turnEvents = eventsBySession.get(group.sessionId) ?? []
                    // Split the turn into phases so the in-between work
                    // (thinking / tool calls) renders BETWEEN the assistant's
                    // messages instead of being squashed into one "Done" row
                    // at the top.
                    const phases = splitTurnIntoPhases(
                      turnEvents,
                      group.assistantBubbles,
                    )
                    // Card 7 (cursor-native-chat-ux): the LATEST turn group is
                    // the "live" turn whose ActivityPill animates the elapsed
                    // counter + shimmer. All older groups freeze their pill on
                    // the last tool's terminal state. We compare against the
                    // visible window's last index — turns outside the lazy
                    // render window aren't mounted so they don't matter here.
                    const isLatestGroup = groupIdx === visibleTurnGroups.length - 1
                    const hasAnyPersistedTurnSurface =
                      group.session !== null || turnEvents.length > 0
                    // An optimistic-only group (the user JUST submitted; no
                    // session row exists yet) carries an "optimistic:*"
                    // sessionId and a null session — that one should still
                    // animate as live. A group with a real sessionId but a
                    // null session is a HISTORICAL turn whose session row
                    // simply isn't in the conversation detail payload — it is
                    // NOT live. Defaulting a null session to {@code Running}
                    // was the bug behind the perpetual 100ms ticker, so we
                    // pick a terminal/neutral default ({@code Succeeded})
                    // instead and gate true liveness on {@code group.session}
                    // actually existing (or this being the optimistic group).
                    const isOptimisticGroup =
                      group.session === null &&
                      group.sessionId.startsWith('optimistic:')
                    const sessionStatus =
                      group.session?.status ?? AgentSessionStatus.Succeeded
                    const isFailed =
                      sessionStatus === AgentSessionStatus.Failed
                    // Card 4 (cursor-native-chat-ux): the session-level
                    // failureReason is now authoritative — the per-event
                    // failure walker was retired with the old wire union.
                    // The terminal Status event carries a {@code message}
                    // string but card 6 surfaces that via the activity pill
                    // state machine, not via this fallback. Card 7 retired
                    // the local retry handler — see the "failure surfaces"
                    // comment inside the phase render below.
                    return (
                      <Box
                        key={group.sessionId}
                        sx={{ display: 'flex', flexDirection: 'column', gap: 1.25 }}
                      >
                        {/* "Load earlier" — surfaces at the TOP only when this
                            session's loaded window may have OLDER events before
                            it (last page was full === PAGE_LIMIT). Tail-first
                            hydration loads the newest events; scrolling UP to
                            this sentinel pages BACKWARD and prepends the older
                            chunk (scroll position preserved by handleLoadEarlier). */}
                        {hasMoreBySession.has(group.sessionId) && (
                          <LoadEarlierSentinel
                            sessionId={group.sessionId}
                            loading={loadingMoreSessions.has(group.sessionId)}
                            onLoadEarlier={handleLoadEarlier}
                          />
                        )}
                        {group.userBubbles.map((m) => {
                          // Only the synthesised "session prompt" bubble at
                          // sequence 0 represents the user's submitted message
                          // for a queued session — extra user-kind events
                          // inside the same session (rare) should not surface
                          // a revoke. The id pattern is locked to "session:*:prompt".
                          const isSessionPrompt = m.id.endsWith(':prompt')
                          const queuedForBubble =
                            isSessionPrompt &&
                            queuedSessionIds.has(group.sessionId)
                          // Resolve the per-turn model label here so the
                          // bubble itself can stay generic. Only the prompt
                          // bubble carries the pill — extra user-kind events
                          // inside the same session (rare) inherit nothing.
                          const modelMeta = isSessionPrompt
                            ? resolveSessionModel(group.session)
                            : { label: null, slug: null }
                          // The per-turn cost rides inside the user prompt
                          // bubble as a small whisper in the bottom-right
                          // (replacing the older top-right-of-turn standalone
                          // badge below the assistant prose). Only attach the
                          // cost payload to the session-prompt bubble — extra
                          // user-kind events inside the same session must not
                          // duplicate the annotation.
                          const sessionCostForBubble =
                            isSessionPrompt && group.session
                              ? {
                                  costUsd: group.session.costUsd,
                                  inputTokens: group.session.inputTokens,
                                  outputTokens: group.session.outputTokens,
                                  cacheReadTokens:
                                    group.session.cacheReadTokens,
                                  cacheWriteTokens:
                                    group.session.cacheWriteTokens,
                                  reasoningTokens:
                                    group.session.reasoningTokens,
                                }
                              : null
                          return (
                            <MessageBubble
                              key={m.id}
                              message={m}
                              isQueued={queuedForBubble}
                              modelLabel={modelMeta.label}
                              modelSlug={modelMeta.slug}
                              onRevoke={
                                queuedForBubble
                                  ? (text) =>
                                      void handleRevoke(group.sessionId, text)
                                  : undefined
                              }
                              sessionCost={sessionCostForBubble}
                            />
                          )
                        })}
                        {/* Past attachment chips (chat-file-attachments).
                            Render one PastAttachmentChip per attachment sent on
                            this turn, right-aligned under the user bubble to
                            mirror its alignment. Each chip lazily fetches a
                            fresh presigned download URL on click. */}
                        {group.session?.attachments &&
                          group.session.attachments.length > 0 && (
                            <Box
                              sx={{
                                display: 'flex',
                                flexWrap: 'wrap',
                                justifyContent: 'flex-end',
                                gap: 0.75,
                                mt: -0.5,
                              }}
                            >
                              {group.session.attachments.map((att) => (
                                <PastAttachmentChip
                                  key={att.id}
                                  attachmentId={att.id}
                                  fileName={att.fileName}
                                  sizeBytes={att.sizeBytes}
                                  contentType={att.contentType}
                                />
                              ))}
                            </Box>
                          )}
                        {(() => {
                        // For the live (latest in-flight) turn, the assistant
                        // content below the user bubble is wrapped in a measured
                        // node so the tail spacer can shrink 1:1 as the response
                        // streams in (see liveContentRef / liveContentHeightPx).
                        // Other groups render the phases bare — no measurement.
                        const isLiveTurnContent =
                          isLatestGroup && isLatestTurnLive
                        const phasesContent = phases.map((phase, phaseIndex) => {
                          const isLastPhase = phaseIndex === phases.length - 1
                          const phaseKey = `${group.sessionId}:${phaseIndex}`
                          void phaseKey // historical key — chevron state now
                          // lives inside TurnPhaseChrome.
                          // Card 7: this phase is "live" iff it's the last
                          // phase of the LATEST turn group AND the session is
                          // still in flight. Older phases freeze their pill on
                          // their last tool's terminal state per spec §3.
                          // A phase is live ONLY for the latest group's last
                          // non-closed phase, and ONLY when either:
                          //   * this is the optimistic just-submitted turn (no
                          //     session row yet), or
                          //   * a real session row exists AND its status is
                          //     genuinely in-flight.
                          // A historical group with a null session (sessionId
                          // present but not in the conversation payload) can
                          // never be live — this is what stops the perpetual
                          // elapsed-counter interval on idle/finished turns.
                          const phaseIsLive =
                            isLastPhase &&
                            isLatestGroup &&
                            !phase.isClosed &&
                            (isOptimisticGroup ||
                              (group.session != null &&
                                (sessionStatus === AgentSessionStatus.Running ||
                                  sessionStatus === AgentSessionStatus.Pending ||
                                  sessionStatus ===
                                    AgentSessionStatus.Canceling)))
                          // Terminal status for the footer (last phase only).
                          // Prefer the wire status event if we have it (most
                          // precise — Cancelled / Error / Expired are
                          // distinguishable); fall back to the SessionSummary
                          // status if no terminal status row arrived yet.
                          const wireTerminal = deriveTerminalStatusFromEvents(
                            phase.events,
                          )
                          const fallbackTerminal =
                            sessionStatus === AgentSessionStatus.Succeeded
                              ? 'Finished'
                              : sessionStatus === AgentSessionStatus.Canceled
                                ? 'Cancelled'
                                : sessionStatus === AgentSessionStatus.Failed
                                  ? 'Error'
                                  : null
                          const phaseTerminalStatus =
                            isLastPhase && !phaseIsLive
                              ? wireTerminal ?? fallbackTerminal
                              : null
                          // Phase chrome renders when:
                          //   1. There's real work to surface in this phase
                          //      (events.length > 0), OR
                          //   2. It's the last phase, has no closing bubble
                          //      AND the turn is persisted — surfaces the
                          //      pill so the user knows the turn happened
                          //      (queued, failed mid-tool, canceled), OR
                          //   3. It's the last phase and the session itself
                          //      failed or was canceled — even if a bubble
                          //      streamed in, we still surface the pill row
                          //      above it so the state is unambiguous.
                          //
                          // For a vanilla "say hi → get hi back" turn the
                          // bubble alone communicates completion; we skip
                          // the pill entirely.
                          const isCanceled =
                            sessionStatus === AgentSessionStatus.Canceled ||
                            sessionStatus === AgentSessionStatus.Canceling
                          const showPhaseChrome =
                            phase.events.length > 0 ||
                            (isLastPhase &&
                              phase.bubbles.length === 0 &&
                              hasAnyPersistedTurnSurface) ||
                            (isLastPhase && (isFailed || isCanceled))
                          // Cancel handler — only wired on a live phase.
                          const phaseCancelHandler =
                            phaseIsLive && group.session
                              ? () => void handleStop(group.session!.id)
                              : undefined
                          // Card 8 (cursor-native-chat-ux §7): show
                          // "Cancelling…" optimistically the instant the
                          // user clicks the pill's cancel control. Two
                          // signals feed it:
                          //   * the local optimistic set populated by
                          //     {@link handleStop} (covers the gap between
                          //     click and the daemon's Cancelled event), OR
                          //   * the session's persisted Canceling status
                          //     (covers a tab opened mid-cancel where no
                          //     local click occurred — sessions list query
                          //     hydrates the Canceling state on mount).
                          //
                          // The override is suppressed by TurnPhaseChrome
                          // if the derived pill state is already terminal
                          // (Cancelled / run-error / Expired) so the
                          // wire-truth always wins on race.
                          const phaseOptimisticCancelling =
                            isLastPhase &&
                            (cancellingSessionIds.has(group.sessionId) ||
                              sessionStatus ===
                                AgentSessionStatus.Canceling)
                          return (
                            <Box
                              key={phaseKey}
                              sx={{
                                display: 'flex',
                                flexDirection: 'column',
                                gap: 1.25,
                              }}
                            >
                              {showPhaseChrome && (
                                <PhaseChromeWithRunResult
                                  sessionId={group.sessionId}
                                  events={phase.events}
                                  isLiveTurn={phaseIsLive}
                                  isLastPhase={isLastPhase}
                                  terminalStatus={phaseTerminalStatus}
                                  shouldQueryRunResult={
                                    isLastPhase &&
                                    !phaseIsLive &&
                                    !!phaseTerminalStatus &&
                                    !!group.session
                                  }
                                  onCancel={phaseCancelHandler}
                                  optimisticCancelling={
                                    phaseOptimisticCancelling
                                  }
                                />
                              )}
                              {phase.bubbles.map((m) => (
                                <MessageBubble key={m.id} message={m} />
                              ))}
                              {/* Card 7: failure surfaces (failureReason,
                                  retry link, queue/warming labels) used to
                                  live on the old TurnStatusLine. They've
                                  moved into the run-level terminal pill
                                  states (run-error / cancelled / expired)
                                  and the TurnFooter ("Stopped after Xs"
                                  reads as the canonical Error verb). The
                                  retry/queue/warming affordances are
                                  intentionally not surfaced inside the new
                                  pill chrome — wire them through in a
                                  follow-up if user testing shows the
                                  workspace needs them back. */}
                            </Box>
                          )
                        })
                        if (!isLiveTurnContent) return phasesContent
                        return (
                          <Box
                            ref={liveContentRef}
                            sx={{
                              display: 'flex',
                              flexDirection: 'column',
                              gap: 1.25,
                            }}
                          >
                            {phasesContent}
                          </Box>
                        )
                        })()}
                      </Box>
                    )
                  })}
                  {/* Inline runtime spec proposal cards. When the agent
                      submits a proposal via `propose_runtime_spec`, the row
                      lands here so the user can approve / edit / reject
                      without leaving the chat. Status transitions (Approved
                      → Applied, Failed, etc.) are driven by RuntimeProposalUpdated
                      fan-outs which {@code useProposalSignalR} invalidates. */}
                  {pendingProposals.length > 0 && (
                    <Box
                      sx={{
                        display: 'flex',
                        flexDirection: 'column',
                        gap: 1,
                      }}
                    >
                      {pendingProposals.map((proposal) => (
                        <RuntimeProposalCard
                          key={`runtime-proposal:${proposal.id}`}
                          proposal={proposal}
                          projectId={projectId}
                        />
                      ))}
                    </Box>
                  )}
                  {/* Live-turn tail spacer — gives the on-send "scroll user
                      bubble to top" somewhere to scroll TO. DYNAMIC height:
                      the reserved room (clamp(220px, 45vh, 440px)) is
                      consumed 1:1 by the live turn's assistant content as it
                      streams in, so the message fills DOWNWARD to the real
                      scroll bottom instead of floating ~45vh above it. At
                      send the content is ~0 → spacer ≈ reserved room so the
                      user bubble can reach the top; once the content exceeds
                      the reserved room the spacer collapses to 0 and the
                      transcript scrolls normally. Collapsed to 0 when the
                      latest turn is dormant. The sticky-when-at-bottom hook
                      handles auto-snap during streaming. */}
                  {visibleTurnGroups.length > 0 && (
                    <Box
                      aria-hidden
                      data-testid="chat-tail-spacer"
                      sx={{
                        height: isLatestTurnLive
                          ? `max(0px, calc(clamp(220px, 45vh, 440px) - ${liveContentHeightPx}px))`
                          : 0,
                        flexShrink: 0,
                        overflow: 'hidden',
                      }}
                    />
                  )}
                </Stack>
              )}
              <div ref={bottomRef} />
            </Box>
          </Box>
            {/* Jump-to-bottom pill. Renders inside the relative wrapper so
                it floats over the bottom-right of the scroll viewport. Only
                visible when the user has scrolled away from the bottom —
                clicking smooth-scrolls back to the bottom and re-pins. */}
            {!isAtBottom && (
              <Tooltip title="Jump to latest" placement="top">
                <Fab
                  size="small"
                  aria-label="Jump to latest message"
                  onClick={() => scrollToBottom('smooth')}
                  sx={{
                    // Horizontally centered in the scroll viewport so the
                    // affordance sits along the transcript's natural reading
                    // axis instead of getting lost in the right margin.
                    position: 'absolute',
                    bottom: 16,
                    left: '50%',
                    transform: 'translateX(-50%)',
                    backgroundColor: 'instrument.surface',
                    color: tokens.textPrimary,
                    border: `1px solid ${tokens.hairline}`,
                    boxShadow: '0 2px 8px rgba(0, 0, 0, 0.08)',
                    '&:hover': {
                      backgroundColor: 'instrument.surface',
                      color: tokens.accent,
                      borderColor: tokens.accentBorderStrong,
                    },
                  }}
                >
                  <KeyboardArrowDownIcon fontSize="small" />
                </Fab>
              </Tooltip>
            )}
          </Box>

          {/* Out-of-sync-with-GitHub banner. Shown the moment a commit /
              push / merge fails, cleared on the next successful push. The
              toast at the moment of failure is transient; this banner is the
              persistent surface so the user can't miss that the daemon is
              not in sync with the remote. */}
          <GitSyncBanner
            error={gitSyncError}
            onDismiss={() => setGitSyncError(null)}
          />

          {/* Branch-divergent banner. Surfaces when the daemon's first-session
              fast-forward pull from origin failed because the histories have
              diverged (see daemon-git-sync-redesign spec, Scene 5). Read-only
              in v1 — the user resolves the divergence in their own local
              tooling and then starts a new session, which will boot normally
              once the FF pull succeeds. Sits in the same column as the
              composer so the layout stays calm when it appears / disappears. */}
          {showBranchDivergentBanner && <BranchDivergentBanner />}

          {/* Composer */}
          <Composer
            value={composerValue}
            onChange={setComposerValue}
            onKeyDown={handleKeyDown}
            onSubmit={() => void handleSubmit()}
            canSubmit={canSubmit}
            sending={sending}
            isConnected={isConnected}
            disabledReason={sendDisabledReason}
            placeholder={composerPlaceholder}
            focusKey={conversationId}
            onStop={stopOnClick}
            stopping={stoppingForButton}
            projectId={projectId}
            conversationId={conversationId ?? null}
            externalInputRef={composerInputRef}
            onFocusChange={onComposerFocusChange}
            attachments={composerAttachments}
          />
        </>
      )}
    </Box>
  )
}

// ── Git out-of-sync banner ─────────────────────────────────────────────────
//
// Rendered above the composer whenever the daemon last reported a commit /
// push / merge failure that hasn't been cleared by a subsequent push success.
// The user can click "Details" to expand the captured output tail (the same
// tail surfaced via the operator audit), or dismiss the banner manually.
//
// Visual style: chrome-aware Alert with a "warning" severity so it
// reads as actionable but non-blocking. Sits in the same column as the
// composer (no extra wrapper) so the layout doesn't shift on appear.
function GitSyncBanner({
  error,
  onDismiss,
}: {
  error: { title: string; detail: string; reason?: string } | null
  onDismiss: () => void
}) {
  // When the daemon couldn't classify the failure ('Unknown'), the title has no
  // actionable hint — the raw output tail is the only signal, so open it by
  // default. Otherwise start collapsed.
  const isUnknown = error?.reason === 'Unknown'
  const [expanded, setExpanded] = useState(isUnknown)
  // Reset the "Details" expanded state whenever the banner content changes —
  // a fresh error shouldn't surface the previous error's tail. Re-apply the
  // auto-open for unclassified failures.
  useEffect(() => {
    setExpanded(isUnknown)
  }, [error?.title, isUnknown])

  if (!error) return null
  return (
    <Box sx={{ px: { xs: 2, md: 4 }, pt: 1 }}>
      <Box sx={{ maxWidth: 760, mx: 'auto' }}>
        <Alert
          severity="warning"
          variant="outlined"
          onClose={onDismiss}
          sx={{ alignItems: 'flex-start' }}
          action={
            <Stack direction="row" spacing={1} alignItems="center">
              <Button
                size="small"
                color="inherit"
                onClick={() => setExpanded((v) => !v)}
              >
                {expanded ? 'Hide' : 'Details'}
              </Button>
            </Stack>
          }
        >
          {/* Lead with the actual classified failure (the specific reason),
              not a generic "out of sync" headline that hides what really
              broke. The GitHub-sync framing is demoted to an eyebrow. */}
          <Typography
            variant="caption"
            sx={{ color: 'text.secondary', fontWeight: 600, letterSpacing: '0.04em', textTransform: 'uppercase' }}
          >
            GitHub sync
          </Typography>
          <AlertTitle sx={{ mb: 0, mt: 0.25 }}>
            {error.title}
          </AlertTitle>
          <Collapse in={expanded} timeout="auto" unmountOnExit>
            <Box
              component="pre"
              sx={{
                mt: 1,
                p: 1,
                fontSize: 12,
                fontFamily:
                  'ui-monospace, SFMono-Regular, Menlo, monospace',
                backgroundColor: 'rgba(0,0,0,0.04)',
                borderRadius: 1,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                maxHeight: 200,
                overflow: 'auto',
              }}
            >
              {error.detail}
            </Box>
          </Collapse>
        </Alert>
      </Box>
    </Box>
  )
}

// ── Branch-divergent banner ────────────────────────────────────────────────
//
// Rendered above the composer when the latest session terminated with
// {@code failureReason === 'branch_divergent'} — the daemon's first-session
// fast-forward pull from origin failed because the local volume's history and
// the remote branch have diverged (see the daemon-git-sync-redesign spec,
// Scene 5). The banner is informational only: no buttons, no auto-resolve.
// The user resolves the divergence in their own tooling (rebase, merge, or
// force-push as appropriate) and then starts a new session, which boots
// normally once the FF pull succeeds.
//
// Visual style mirrors {@link GitSyncBanner} for consistency: a chrome
// aware outlined Alert with "warning" severity, centered in the same column
// as the composer so the layout doesn't jump when it appears / disappears.
function BranchDivergentBanner() {
  return (
    <Box sx={{ px: { xs: 2, md: 4 }, pt: 1 }}>
      <Box sx={{ maxWidth: 760, mx: 'auto' }}>
        <Alert
          severity="warning"
          variant="outlined"
          sx={{ alignItems: 'flex-start' }}
        >
          <AlertTitle sx={{ mb: 0 }}>
            ⚠ Branch is out of sync with origin
          </AlertTitle>
          <Typography variant="body2" sx={{ mt: 0.5 }}>
            Someone pushed commits to this branch outside this runtime.
            Resolve the divergence in your local tooling (rebase, merge, or
            force-push as appropriate), then start a new session.
          </Typography>
        </Alert>
      </Box>
    </Box>
  )
}

// ── Stale conversation (404) ───────────────────────────────────────────────
//
// ?c={id} pointed at a conversation that was archived or removed (likely by
// another tab) — the conversation-by-id query returned 404. Rather than
// silently falling back to the empty state, surface a one-line explanation
// + a button that drops ?c= so the user lands on a fresh empty state for
// the current branch. Styling mirrors {@link EmptyStateBody} so the canvas
// reads consistently.
function StaleConversationBody({ onStartNew }: { onStartNew: () => void }) {
  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        alignItems: 'center',
        px: { xs: 2, md: 4 },
        py: { xs: 4, md: 6 },
      }}
    >
      <Box sx={{ width: '100%', maxWidth: 640, textAlign: 'center' }}>
        <Typography
          component="h1"
          sx={{
            fontSize: { xs: '1.25rem', md: '1.5rem' },
            fontWeight: 400,
            letterSpacing: '-0.01em',
            color: tokens.textPrimary,
            mb: 1.25,
          }}
        >
          This conversation is no longer available.
        </Typography>
        <Box sx={{ mt: 2 }}>
          <Button
            variant="outlined"
            onClick={onStartNew}
            sx={{
              textTransform: 'none',
              borderColor: tokens.hairline,
              color: tokens.textPrimary,
            }}
          >
            Start a new conversation
          </Button>
        </Box>
      </Box>
    </Box>
  )
}

// ── Empty state ────────────────────────────────────────────────────────────

interface EmptyStateBodyProps {
  projectName: string
  branchName: string
  composerValue: string
  onChange: (v: string) => void
  onKeyDown: (e: React.KeyboardEvent<HTMLDivElement>) => void
  onSubmit: () => void
  canSubmit: boolean
  sending: boolean
  isConnected: boolean
  sendDisabledReason: string | null
  /** Wake-aware composer placeholder lifted from the parent. */
  placeholder: string
  /** Threaded through to the inline model picker under the composer. */
  projectId: string
  /** Always {@code null} in this empty-state variant — kept for prop parity. */
  conversationId: string | null
  /**
   * Forwarded to the inner Composer so the parent canvas can imperatively
   * focus the textarea (used after the "+ New conversation" pivot).
   */
  composerInputRef?: React.MutableRefObject<
    HTMLTextAreaElement | HTMLInputElement | null
  >
  /**
   * Optional focus-state notifier forwarded to the inner Composer. Mobile
   * callers wire this to collapse the bottom tab bar while typing.
   */
  onComposerFocusChange?: (focused: boolean) => void
  /** Attachment wiring forwarded to the inner Composer. */
  attachments?: ComposerAttachmentsProps
}

function EmptyStateBody({
  projectName,
  branchName,
  composerValue,
  onChange,
  onKeyDown,
  onSubmit,
  canSubmit,
  sending,
  isConnected,
  sendDisabledReason,
  placeholder,
  projectId,
  conversationId,
  composerInputRef,
  onComposerFocusChange,
  attachments,
}: EmptyStateBodyProps) {
  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        alignItems: 'center',
        px: { xs: 2, md: 4 },
        py: { xs: 4, md: 6 },
      }}
    >
      <Box sx={{ width: '100%', maxWidth: 640, textAlign: 'center', mb: 4 }}>
        <Typography
          component="h1"
          sx={{
            fontSize: { xs: '1.75rem', md: '2.125rem' },
            fontWeight: 300,
            letterSpacing: '-0.02em',
            color: tokens.textPrimary,
            mb: 1.25,
          }}
        >
          {projectName}
        </Typography>
        <Typography
          sx={{
            fontSize: '0.9375rem',
            color: tokens.textMuted,
            letterSpacing: '-0.005em',
          }}
        >
          Start a new conversation on the {branchName} branch.
        </Typography>
      </Box>
      <Box sx={{ width: '100%', maxWidth: 640 }}>
        <Composer
          value={composerValue}
          onChange={onChange}
          onKeyDown={onKeyDown}
          onSubmit={onSubmit}
          canSubmit={canSubmit}
          sending={sending}
          isConnected={isConnected}
          disabledReason={sendDisabledReason}
          placeholder={placeholder}
          autoFocus
          embedded
          projectId={projectId}
          conversationId={conversationId}
          externalInputRef={composerInputRef}
          onFocusChange={onComposerFocusChange}
          attachments={attachments}
        />
      </Box>
    </Box>
  )
}

// ── Composer ───────────────────────────────────────────────────────────────

/**
 * Attachment wiring (chat-file-attachments) threaded into the Composer. The
 * parent ChatCanvas owns all the state (pending slots, drag highlight, the
 * minted conversation id); the Composer is a thin view that renders the
 * paperclip picker, the pending-slot strip, and a drag-and-drop highlight,
 * forwarding user intent back up via the callbacks.
 */
interface ComposerAttachmentsProps {
  /**
   * The conversation id every {@link PendingAttachmentSlot} presigns against.
   * Null until the user attaches their first file in the empty state (the
   * parent mints one on file-pick). The Composer renders pending slots only
   * once this is non-null.
   */
  conversationId: string | null
  /** Branch id — scopes the SignalR staging-ack filter inside each slot. */
  branchId: string
  /** Front-end-keyed pending files; one {@link PendingAttachmentSlot} each. */
  pending: Array<{ slotId: string; file: File }>
  /** Lifts each slot's (state, attachmentId) transition to the parent. */
  onSlotStateChange: (slotId: string, snapshot: SlotStateSnapshot) => void
  /** Parent removes the slot from its array, which unmounts the chip. */
  onRemove: (slotId: string) => void
  /** Below-input hint while uploads / staging are in flight (or null). */
  helperText: string | null
  /** True while a file drag is hovering the composer — drives the highlight. */
  isDragOver: boolean
  /** Common entry point for both the picker and a drop. */
  onFilesPicked: (files: FileList | File[] | null) => void
  onDragEnter: (e: React.DragEvent<HTMLDivElement>) => void
  onDragOver: (e: React.DragEvent<HTMLDivElement>) => void
  onDragLeave: (e: React.DragEvent<HTMLDivElement>) => void
  onDrop: (e: React.DragEvent<HTMLDivElement>) => void
}

interface ComposerProps {
  value: string
  onChange: (v: string) => void
  onKeyDown: (e: React.KeyboardEvent<HTMLDivElement>) => void
  onSubmit: () => void
  canSubmit: boolean
  sending: boolean
  isConnected: boolean
  disabledReason: string | null
  /**
   * Placeholder text. Lifted to the parent so the wake-aware Suspended copy
   * can shadow the default. Falls back to the historical "Send a message…" /
   * "Connecting…" pair when omitted.
   */
  placeholder?: string
  /** Auto-focus on mount — used by the empty-state variant. */
  autoFocus?: boolean
  /**
   * Whenever this key changes, the composer re-focuses its input. Used by
   * the transcript variant to focus on conversation switch (P4.2).
   * Passing {@code null} or omitting it disables this behaviour.
   */
  focusKey?: string | null
  /**
   * Embedded variant skips the canvas-spanning top border (it's already
   * centered inside the empty-state composition).
   */
  embedded?: boolean
  /**
   * Stop affordance. Rendered to the left of the send button when the
   * latest session is in-flight (Running). Tapping it cancels the running
   * turn via the AgentHub. {@code null}/{@code undefined} hides the button —
   * the standard idle composer.
   */
  onStop?: () => void
  /**
   * True while the cancel is in flight (server has transitioned the session
   * to {@code Canceling} and we're waiting on the daemon's terminal event).
   * The Stop button shows a spinner and is disabled until the session lands
   * in a terminal state.
   */
  stopping?: boolean
  /**
   * The project the composer is anchored on — used by the inline model
   * picker tucked below the input to resolve the project default model.
   */
  projectId: string
  /**
   * Currently-active conversation. {@code null} = draft / empty-canvas;
   * the inline model picker stays hidden in that case because the override
   * is keyed off the conversation id.
   */
  conversationId: string | null
  /**
   * Optional ref forwarded by the parent so it can imperatively focus the
   * underlying input (e.g. after "+ New conversation" clears the URL and
   * mounts the empty state). Merged with the Composer's own internal ref.
   */
  externalInputRef?: React.MutableRefObject<
    HTMLTextAreaElement | HTMLInputElement | null
  >
  /**
   * Optional focus-state notifier for the textarea. Mobile callers use this
   * to collapse the bottom tab bar while typing so the on-screen keyboard
   * has more vertical room. Desktop callers omit it.
   *
   * <p>The blur transition is debounced (~150ms) so quick focus hand-offs to
   * adjacent affordances (e.g. tapping the Send button) don't briefly flash
   * the tab bar back into view.</p>
   */
  onFocusChange?: (focused: boolean) => void
  /**
   * Attachment wiring (chat-file-attachments). When supplied, the Composer
   * renders a working paperclip picker, a pending-slot strip above the
   * controls row, and a drag-and-drop highlight over the input card. Omitted
   * for callers that don't support attachments.
   */
  attachments?: ComposerAttachmentsProps
}

function Composer({
  value,
  onChange,
  onKeyDown,
  onSubmit,
  canSubmit,
  sending,
  isConnected,
  disabledReason,
  placeholder,
  autoFocus,
  focusKey,
  embedded,
  onStop,
  stopping,
  projectId,
  conversationId,
  externalInputRef,
  onFocusChange,
  attachments,
}: ComposerProps) {
  // Hidden file <input> backing the paperclip picker. Reset to '' after each
  // pick so selecting the same file twice in a row still fires onChange.
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  // Imperative focus management for conversation-switch (focusKey changes).
  // We can't rely on {@code autoFocus} alone because the transcript variant
  // stays mounted across conversation switches — autoFocus only fires on
  // first mount. Hooking a ref to the underlying input lets us focus on
  // demand, which is the calm "open and you can type" feel the rehydration
  // moment needs.
  const inputRef = useRef<HTMLTextAreaElement | HTMLInputElement | null>(null)
  useEffect(() => {
    if (focusKey === undefined || focusKey === null) return
    inputRef.current?.focus()
  }, [focusKey])
  // Fan the underlying input node out to both the internal ref (used by the
  // focusKey effect above) and the optional parent-supplied ref so callers
  // can drive focus from outside the Composer.
  const setInputRef = useCallback(
    (node: HTMLTextAreaElement | HTMLInputElement | null) => {
      inputRef.current = node
      if (externalInputRef) {
        externalInputRef.current = node
      }
    },
    [externalInputRef],
  )
  // Debounced focus-state signal for {@code onFocusChange}. We schedule the
  // "focused=false" notification on blur and cancel it on the next focus so
  // brief focus hand-offs to nearby affordances (e.g. the Send button) don't
  // briefly flash the mobile tab bar back into view. Cleared on unmount.
  //
  // {@code isMountedRef} guards against the {@code autoFocus}-induced focus
  // event that fires during the initial DOM commit — that one isn't a user
  // action and shouldn't trigger the "hide tab bar" signal. focus events
  // dispatch synchronously during commit; useEffect runs after paint, so by
  // the time {@code isMountedRef.current} flips to true, the autoFocus
  // focus event has already been swallowed. All subsequent real user
  // focuses (tap, keyboard Tab, programmatic re-focus on conversation
  // switch) pass through normally.
  const blurTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isMountedRef = useRef(false)
  useEffect(() => {
    isMountedRef.current = true
    return () => {
      if (blurTimeoutRef.current !== null) {
        clearTimeout(blurTimeoutRef.current)
        blurTimeoutRef.current = null
      }
    }
  }, [])
  const handleInputFocus = useCallback(() => {
    if (!isMountedRef.current) return
    if (blurTimeoutRef.current !== null) {
      clearTimeout(blurTimeoutRef.current)
      blurTimeoutRef.current = null
    }
    onFocusChange?.(true)
  }, [onFocusChange])
  const handleInputBlur = useCallback(() => {
    if (!onFocusChange) return
    if (blurTimeoutRef.current !== null) {
      clearTimeout(blurTimeoutRef.current)
    }
    blurTimeoutRef.current = setTimeout(() => {
      blurTimeoutRef.current = null
      onFocusChange(false)
    }, 150)
  }, [onFocusChange])
  // Send button — square 32x32 with 8px radius, dark surface inverted text.
  // Matches the reference design's filled `t.text` send affordance: reads as
  // the canonical commit action without competing with the model/yolo chips
  // for visual weight.
  const sendButton = (
    <IconButton
      onClick={onSubmit}
      disabled={!canSubmit}
      aria-label="Send message"
      sx={{
        width: 32,
        height: 32,
        borderRadius: 1,
        flexShrink: 0,
        backgroundColor: canSubmit ? tokens.textPrimary : 'rgba(0,0,0,0.06)',
        color: canSubmit ? tokens.canvasBg : tokens.textFaint,
        transition: 'background-color 200ms ease, color 200ms ease',
        '&:hover': {
          backgroundColor: canSubmit ? '#000' : 'rgba(0,0,0,0.08)',
        },
        '&.Mui-disabled': {
          backgroundColor: 'rgba(0,0,0,0.06)',
          color: tokens.textFaint,
        },
      }}
    >
      {sending ? (
        <CircularProgress size={14} thickness={5} sx={{ color: 'inherit' }} />
      ) : (
        <SendIcon sx={{ fontSize: 15 }} />
      )}
    </IconButton>
  )

  // Stop button — visible only when a turn is in flight. While the cancel
  // is propagating ({@code stopping}) we keep the button rendered but
  // disabled with a spinner so the user can see their click took effect
  // (the server has already transitioned the session to Canceling). Sized to
  // 32x32 / radius 8 so it sits flush next to the {@code sendButton}.
  const stopButton = onStop ? (
    <Tooltip title={stopping ? 'Canceling…' : 'Stop'} placement="top">
      <span>
        <IconButton
          onClick={stopping ? undefined : onStop}
          disabled={stopping === true}
          aria-label="Stop current turn"
          sx={{
            width: 32,
            height: 32,
            borderRadius: 1,
            flexShrink: 0,
            backgroundColor: tokens.surface,
            color: tokens.textPrimary,
            border: `1px solid ${tokens.hairline}`,
            transition: 'background-color 200ms ease, border-color 200ms ease',
            '&:hover': {
              backgroundColor: 'rgba(0,0,0,0.04)',
              borderColor: 'rgba(0,0,0,0.18)',
            },
            '&.Mui-disabled': {
              backgroundColor: tokens.surface,
              color: tokens.textFaint,
              borderColor: tokens.hairline,
            },
          }}
        >
          {stopping ? (
            <CircularProgress size={14} thickness={5} sx={{ color: 'inherit' }} />
          ) : (
            <StopIcon sx={{ fontSize: 16 }} />
          )}
        </IconButton>
      </span>
    </Tooltip>
  ) : null

  // Tiny inline `<kbd>` recipe for the "⌘ + ↵ to send" hint in the bottom
  // controls row. Matches the reference {@code kbd(t)} factory: a 4px-radius
  // chip-bg pill with a hairline border, mono numeric, 10.5px text. Looks
  // like a keyboard cap without trying too hard.
  const kbdSx = {
    display: 'inline-block',
    padding: '1px 5px',
    mx: '1px',
    fontSize: '10.5px',
    fontFamily: workspaceFontFamily.mono,
    backgroundColor: tokens.chipBg,
    color: tokens.textMuted,
    border: `1px solid ${tokens.hairline}`,
    borderRadius: '4px',
    fontWeight: 500,
    lineHeight: 1,
  } as const

  // Small chip-shaped icon button used for the attach + slash affordances in
  // the bottom control row. Matches the reference {@code composerChip} recipe:
  // 28x28 with a hairline border, no background until hover, calm hairline
  // edges, muted ink. The control row uses {@code gap: 6} so the chips read
  // as a related cluster rather than free-floating icons.
  const chipSx = {
    width: 28,
    height: 28,
    minWidth: 28,
    p: 0,
    borderRadius: 1,
    border: `1px solid ${tokens.hairline}`,
    backgroundColor: tokens.surface,
    color: tokens.textMuted,
    flexShrink: 0,
    transition: 'background-color 120ms ease, border-color 120ms ease, color 120ms ease',
    '&:hover': {
      backgroundColor: tokens.chipHoverBg,
      color: tokens.textPrimary,
    },
  } as const

  return (
    <Box
      sx={{
        flexShrink: 0,
        backgroundColor: 'instrument.chrome',
        borderTop: embedded ? 'none' : `1px solid ${tokens.hairline}`,
        px: embedded ? 0 : { xs: 1.5, md: 1.75 },
        py: embedded ? 0 : { xs: 1.25, md: 1.5 },
      }}
    >
      {/* Composer panel — single rounded card with two stacked rows: a
          textarea zone on top and a controls strip below. Matches the
          reference design's {@code Composer} recipe (chat.jsx). The
          {@code hairlineStrong} border + soft 1px ambient shadow lift the
          card slightly off the chrome surface so it reads as the primary
          input affordance without shouting. */}
      <Box
        onDragEnter={attachments?.onDragEnter}
        onDragOver={attachments?.onDragOver}
        onDragLeave={attachments?.onDragLeave}
        onDrop={attachments?.onDrop}
        sx={{
          maxWidth: '100%',
          mx: 'auto',
          position: 'relative',
          backgroundColor: tokens.surface,
          border: `1px solid ${
            attachments?.isDragOver ? tokens.textPrimary : tokens.hairlineStrong
          }`,
          borderRadius: '14px',
          boxShadow:
            '0 1px 2px rgba(0,0,0,0.04), 0 0 0 1px rgba(0,0,0,0.02)',
          overflow: 'hidden',
          transition: 'border-color 160ms ease, box-shadow 160ms ease',
          '&:focus-within': {
            borderColor: 'rgba(0,0,0,0.22)',
            boxShadow:
              '0 1px 3px rgba(0,0,0,0.06), 0 0 0 1px rgba(0,0,0,0.06)',
          },
        }}
      >
        {/* Drag-over overlay — calm dashed hint that the composer will accept
            the dropped files. Pointer-events:none so the underlying drop
            target (the card) still receives the drop event. */}
        {attachments?.isDragOver && (
          <Box
            sx={{
              position: 'absolute',
              inset: 0,
              zIndex: 2,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              borderRadius: '14px',
              pointerEvents: 'none',
              backgroundColor: 'rgba(0,0,0,0.03)',
              border: `2px dashed ${tokens.textMuted}`,
            }}
          >
            <Typography
              sx={{
                fontSize: '0.8125rem',
                fontWeight: 500,
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
              }}
            >
              Drop files to attach
            </Typography>
          </Box>
        )}

        {/* Pending-attachment strip — one chip per file being uploaded /
            staged. Each PendingAttachmentSlot runs its own upload hook and
            reports state upward for Send-gating. Rendered only once a
            conversation id exists (minted on first file-pick). */}
        {attachments &&
          attachments.pending.length > 0 &&
          attachments.conversationId && (
            <Box
              sx={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 0.75,
                px: '14px',
                pt: '12px',
              }}
            >
              {attachments.pending.map((p) => (
                <PendingAttachmentSlot
                  key={p.slotId}
                  slotId={p.slotId}
                  file={p.file}
                  conversationId={attachments.conversationId as string}
                  branchId={attachments.branchId}
                  onStateChange={attachments.onSlotStateChange}
                  onRemove={attachments.onRemove}
                />
              ))}
            </Box>
          )}

        {/* Top row — textarea zone. Padding mirrors the reference
            {@code '12px 14px 6px'} so the input has breathing room above and
            visually nests against the controls below. */}
        <Box sx={{ px: '14px', pt: '12px', pb: '6px' }}>
          <TextField
            fullWidth
            multiline
            maxRows={6}
            variant="standard"
            autoFocus={autoFocus}
            inputRef={setInputRef}
            placeholder={placeholder ?? (isConnected ? 'Send a message…' : 'Connecting…')}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            onKeyDown={onKeyDown}
            onFocus={handleInputFocus}
            onBlur={handleInputBlur}
            InputProps={{
              disableUnderline: true,
              sx: {
                fontSize: '0.9375rem',
                lineHeight: 1.55,
                letterSpacing: '-0.005em',
                color: tokens.textPrimary,
                fontFamily:
                  '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
                p: 0,
              },
            }}
          />
        </Box>

        {/* Bottom row — controls strip. From left to right: attach, slash,
            divider, model picker (inline ambient), yolo toggle, spacer,
            keyboard hint, optional stop, send. */}
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 0.75,
            px: '10px',
            pb: '10px',
            pt: '8px',
          }}
        >
          {/* Attach — working file picker (chat-file-attachments). The hidden
              <input> is triggered by the chip; selected files are handed to
              the parent which mounts a PendingAttachmentSlot per file. Slash
              remains a disabled stub until that pipeline lands. {@code MUI
              Tooltip} swallows mouse events on disabled children, so the slash
              chip is wrapped in a {@code <span>} to give the tooltip a live
              hover target. */}
          {attachments ? (
            <>
              <input
                ref={fileInputRef}
                type="file"
                multiple
                hidden
                onChange={(e) => {
                  attachments.onFilesPicked(e.target.files)
                  // Reset so re-picking the same file fires onChange again.
                  e.target.value = ''
                }}
              />
              <Tooltip title="Attach files" enterDelay={400}>
                <Box component="span" sx={{ display: 'inline-flex' }}>
                  <IconButton
                    aria-label="Attach file"
                    sx={chipSx}
                    onClick={() => fileInputRef.current?.click()}
                  >
                    <AttachFileIcon sx={{ fontSize: 14 }} />
                  </IconButton>
                </Box>
              </Tooltip>
            </>
          ) : (
            <Tooltip title="Not implemented yet" enterDelay={400}>
              <Box component="span" sx={{ display: 'inline-flex' }}>
                <IconButton aria-label="Attach file" sx={chipSx} disabled>
                  <AttachFileIcon sx={{ fontSize: 14 }} />
                </IconButton>
              </Box>
            </Tooltip>
          )}
          <Tooltip title="Not implemented yet" enterDelay={400}>
            <Box component="span" sx={{ display: 'inline-flex' }}>
              <IconButton
                aria-label="Slash commands"
                sx={chipSx}
                disabled
              >
                <Box
                  component="span"
                  sx={{
                    fontFamily: workspaceFontFamily.mono,
                    fontSize: 12.5,
                    fontWeight: 500,
                    lineHeight: 1,
                  }}
                >
                  /
                </Box>
              </IconButton>
            </Box>
          </Tooltip>

          {/* Vertical hairline separates the static command affordances
              (attach, slash) from the per-conversation state knobs (model,
              yolo) — same {@code 16px} tall sliver used in the chat chrome.
              <p><b>Gotcha:</b> {@code width: 1} in MUI {@code sx} resolves
              to {@code 100%} via the theme spacing multiplier — must use
              the literal string {@code '1px'} for a one-pixel rule, otherwise
              this hairline expands to full row width and crushes everything
              to its right (model picker, yolo, kbd hint, send) out of view. */}
          <Box
            aria-hidden
            sx={{
              width: '1px',
              height: 16,
              backgroundColor: tokens.hairline,
              mx: 0.25,
              flexShrink: 0,
            }}
          />

          <ComposerModelPickerInline
            projectId={projectId}
            conversationId={conversationId}
          />
          <ComposerYoloToggle conversationId={conversationId} />

          {/* Attachment progress whisper — surfaces "Waiting for attachments
              to finish uploading…" / "Preparing attachments for the agent…"
              while uploads are in flight so the user understands why Send is
              gated. Truncates so a long line can't crush the send cluster. */}
          {attachments?.helperText && (
            <Typography
              sx={{
                ml: 1,
                fontSize: '0.6875rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
                whiteSpace: 'nowrap',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                minWidth: 0,
              }}
            >
              {attachments.helperText}
            </Typography>
          )}

          {/* Spacer — pushes the keyboard hint + send cluster to the right
              edge. {@code flex: 1} mirrors the reference. */}
          <Box sx={{ flex: 1 }} />

          {/* Keyboard hint — quiet whisper of "⌘ + ↵ to send". Hidden on
              the narrowest viewports so the send button isn't cramped. */}
          <Box
            sx={{
              display: { xs: 'none', sm: 'inline-flex' },
              alignItems: 'center',
              gap: 0.25,
              fontSize: '0.6875rem',
              color: tokens.textFaint,
              letterSpacing: '-0.005em',
              mr: 0.5,
              flexShrink: 0,
            }}
            aria-hidden
          >
            <Box component="kbd" sx={kbdSx}>
              ⌘
            </Box>
            <Box component="span">+</Box>
            <Box component="kbd" sx={kbdSx}>
              ↵
            </Box>
            <Box component="span" sx={{ ml: 0.25 }}>
              to send
            </Box>
          </Box>

          {stopButton}
          {disabledReason && !canSubmit && !sending ? (
            <Tooltip title={disabledReason} placement="top">
              <span>{sendButton}</span>
            </Tooltip>
          ) : (
            sendButton
          )}
        </Box>
      </Box>
    </Box>
  )
}
