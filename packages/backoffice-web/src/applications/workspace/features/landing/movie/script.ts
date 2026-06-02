/**
 * The landing-page "movie" — a declarative, time-addressed script that replays
 * GlennCode building a waitlist landing page, climaxing in the real (live,
 * wired-to-backend) waitlist form inside the preview panel.
 *
 * Everything here is pure data + pure derivation: {@link deriveMovie} maps an
 * elapsed-ms value to a {@link MovieState}. The render loop ({@code useMovie})
 * just advances elapsed and re-derives — no imperative timers scattered across
 * components, and reduced-motion is a one-liner (jump elapsed to the finale).
 *
 * A {@link MovieFocus} track drives a virtual "camera": the route zooms and
 * centers on whichever panel the system is acting on, so the eye is always on
 * the thing happening right now.
 */

export type AppTab = 'chat' | 'spec' | 'kanban' | 'preview'

export type ChatRole = 'user' | 'assistant'

export interface ChatBubble {
  kind: 'bubble'
  at: number
  role: ChatRole
  /** Plain text; the user bubble is revealed with a typewriter (see typingFor). */
  text: string
}

export interface ChatTool {
  kind: 'tool'
  at: number
  /** Short verb label, e.g. "Writing spec". */
  label: string
  /** Detail line shown muted next to the label. */
  detail: string
  /** Tone — drives the dot color. */
  tone?: 'default' | 'warn' | 'success'
}

export type ChatItem = ChatBubble | ChatTool

export type KanbanColumn = 'backlog' | 'doing' | 'done'

export interface KanbanCardScript {
  id: string
  title: string
  /** Column transitions as [atMs, column] pairs, applied in order. */
  moves: Array<[number, KanbanColumn]>
}

/** Which panel the camera centers + enlarges. */
export type MovieFocus = 'overview' | 'chat' | 'app'

// ── Timeline constants (ms) ──────────────────────────────────────────────────
// Deliberately unhurried — the movie plays once and rests on the finale.

const REPO_AT = 900
const TYPE_START = 2400
const TYPE_END = 5400
export const FINALE_AT = 21800

/** The line the visitor watches type itself in. */
export const USER_MESSAGE = 'Build me a waitlist landing page for my new app.'

// ── Chat transcript ──────────────────────────────────────────────────────────

export const CHAT_ITEMS: ChatItem[] = [
  { kind: 'bubble', at: TYPE_START, role: 'user', text: USER_MESSAGE },
  {
    kind: 'bubble',
    at: 6000,
    role: 'assistant',
    text: "On it. I'll capture emails, persist them, and give you a live preview you can actually test.",
  },
  { kind: 'tool', at: 6700, label: 'Writing spec', detail: 'Waitlist Landing', tone: 'default' },
  { kind: 'bubble', at: 10400, role: 'assistant', text: 'Spec accepted — breaking it into tasks.' },
  { kind: 'tool', at: 11000, label: 'WaitlistForm.tsx', detail: 'email + submit', tone: 'default' },
  { kind: 'tool', at: 12000, label: 'POST /api/waitlist', detail: 'persist signup', tone: 'default' },
  { kind: 'bubble', at: 15800, role: 'assistant', text: 'Running it in the sandbox to check it works…' },
  { kind: 'tool', at: 17000, label: 'Preview', detail: 'empty email slipped through', tone: 'warn' },
  { kind: 'tool', at: 18400, label: 'Fix', detail: 'added email validation', tone: 'success' },
  { kind: 'bubble', at: 19600, role: 'assistant', text: 'Fixed and verified. Here it is — live. Try it 👇' },
]

// ── Spec tab ───────────────────────────────────────────────────────────────

export const SPEC_TITLE = 'Waitlist Landing'
export const SPEC_LINES: Array<{ at: number; text: string }> = [
  { at: 7600, text: 'Hero with a one-line pitch' },
  { at: 8200, text: 'Email capture + “what would you build?”' },
  { at: 8800, text: 'Persist signups to the database' },
  { at: 9400, text: 'Validate email before accepting' },
]
export const SPEC_ACCEPTED_AT = 10000

// ── Kanban tab ───────────────────────────────────────────────────────────────

export const KANBAN_CARDS: KanbanCardScript[] = [
  {
    id: 'form',
    title: 'Waitlist form UI',
    moves: [[11200, 'backlog'], [11800, 'doing'], [13200, 'done']],
  },
  {
    id: 'api',
    title: 'POST /api/waitlist',
    moves: [[11200, 'backlog'], [12600, 'doing'], [14200, 'done']],
  },
  {
    id: 'validate',
    title: 'Email validation',
    moves: [[11200, 'backlog'], [13800, 'doing'], [15200, 'done']],
  },
]

// ── Tab switches ──────────────────────────────────────────────────────────────

const TAB_SWITCHES: Array<[number, AppTab]> = [
  [0, 'preview'],
  [6900, 'spec'],
  [11000, 'kanban'],
  [16000, 'preview'],
]

// ── Camera focus track ─────────────────────────────────────────────────────────

const FOCUS_TRACK: Array<[number, MovieFocus]> = [
  [0, 'overview'],
  [1900, 'chat'], // lean in as the request types itself
  [6900, 'app'], // pan to the spec / tasks / preview panel
  [16800, 'chat'], // pan back to watch the agent test & self-correct
  [19500, 'app'], // settle on the live preview for the finale
]

// ── Captions (the "explaining parts") ─────────────────────────────────────────

const CAPTIONS: Array<[number, string]> = [
  [REPO_AT, 'Point GlennCode at any repo — or start fresh.'],
  [TYPE_START, 'Just describe what you want.'],
  [6900, 'It writes a spec first, so you both agree on the plan.'],
  [11000, 'Then it works the board — one task at a time.'],
  [16000, 'It runs your project in a sandbox and tests it itself.'],
  [16800, 'Short feedback loops: it catches its own bugs and fixes them.'],
  [FINALE_AT, 'This waitlist is real — built live, running in a sandbox. Sign up right inside it.'],
]

// ── Preview phases ─────────────────────────────────────────────────────────────

/** Preview content state derived from elapsed. */
export type PreviewPhase = 'idle' | 'building' | 'live'

// ── Derived state ──────────────────────────────────────────────────────────────

export interface MovieState {
  repoConnected: boolean
  /** Progressive substring of USER_MESSAGE while typing; full string after. */
  typedUser: string
  userTyping: boolean
  /** Chat items whose `at` has elapsed (typing bubble included once started). */
  chat: ChatItem[]
  activeTab: AppTab
  specLines: string[]
  specAccepted: boolean
  kanban: Record<string, KanbanColumn>
  preview: PreviewPhase
  caption: string
  focus: MovieFocus
  /** True once the movie has reached its resting finale frame. */
  atFinale: boolean
}

function lastBefore<T>(pairs: Array<[number, T]>, elapsed: number, fallback: T): T {
  let val = fallback
  for (const [at, v] of pairs) {
    if (elapsed >= at) val = v
    else break
  }
  return val
}

export function deriveMovie(elapsed: number): MovieState {
  const atFinale = elapsed >= FINALE_AT

  // User-message typewriter.
  let typedUser = ''
  let userTyping = false
  if (elapsed >= TYPE_END) {
    typedUser = USER_MESSAGE
  } else if (elapsed >= TYPE_START) {
    const p = (elapsed - TYPE_START) / (TYPE_END - TYPE_START)
    typedUser = USER_MESSAGE.slice(0, Math.ceil(p * USER_MESSAGE.length))
    userTyping = true
  }

  const chat = CHAT_ITEMS.filter((i) => elapsed >= i.at)

  const activeTab = lastBefore(TAB_SWITCHES, elapsed, 'preview')

  const specLines = SPEC_LINES.filter((l) => elapsed >= l.at).map((l) => l.text)
  const specAccepted = elapsed >= SPEC_ACCEPTED_AT

  const kanban: Record<string, KanbanColumn> = {}
  for (const card of KANBAN_CARDS) {
    kanban[card.id] = lastBefore(card.moves, elapsed, 'backlog')
  }

  // Preview: idle until first tab visit, "building" while the agent works/tests,
  // then "live" once the verified message lands.
  let preview: PreviewPhase = 'idle'
  if (elapsed >= 19600) preview = 'live'
  else if (elapsed >= 16000) preview = 'building'

  const caption = lastBefore(CAPTIONS, elapsed, CAPTIONS[0][1])
  const focus = lastBefore(FOCUS_TRACK, elapsed, 'overview')

  return {
    repoConnected: elapsed >= REPO_AT,
    typedUser,
    userTyping,
    chat,
    activeTab,
    specLines,
    specAccepted,
    kanban,
    preview,
    caption,
    focus,
    atFinale,
  }
}

/** The fully-resolved finale frame — used directly for reduced-motion. */
export const FINALE_STATE: MovieState = deriveMovie(FINALE_AT)
