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

const REPO_AT = 1000
const TYPE_START = 2800
const TYPE_END = 6200
export const FINALE_AT = 25600

/** The line the visitor watches type itself in. */
export const USER_MESSAGE = 'Build me a waitlist landing page for my new app.'

/**
 * Cinematic display serif for the landing's narration subtitles + finale
 * headline (Instrument Serif, loaded in index.html). Falls back to a system
 * serif so the cinematic treatment survives even where the CDN is blocked.
 */
export const CINEMATIC_SERIF =
  "'Instrument Serif', 'Fraunces', Georgia, 'Times New Roman', serif"

// ── Chat transcript ──────────────────────────────────────────────────────────

export const CHAT_ITEMS: ChatItem[] = [
  { kind: 'bubble', at: TYPE_START, role: 'user', text: USER_MESSAGE },
  {
    kind: 'bubble',
    at: 7000,
    role: 'assistant',
    text: "On it. I'll capture emails, persist them, and give you a live preview you can actually test.",
  },
  { kind: 'tool', at: 7800, label: 'Writing spec', detail: 'Waitlist Landing', tone: 'default' },
  { kind: 'bubble', at: 13200, role: 'assistant', text: 'Spec accepted — breaking it into tasks.' },
  { kind: 'tool', at: 13900, label: 'WaitlistForm.tsx', detail: 'email + submit', tone: 'default' },
  { kind: 'tool', at: 14900, label: 'POST /api/waitlist', detail: 'persist signup', tone: 'default' },
  { kind: 'bubble', at: 18800, role: 'assistant', text: 'Running it in the sandbox to check it works…' },
  { kind: 'tool', at: 20000, label: 'Preview', detail: 'empty email slipped through', tone: 'warn' },
  { kind: 'tool', at: 21600, label: 'Fix', detail: 'added email validation', tone: 'success' },
  { kind: 'bubble', at: 22800, role: 'assistant', text: 'Fixed and verified. Here it is — live. Try it 👇' },
]

// ── Spec tab ───────────────────────────────────────────────────────────────
// Lines start only after the (slower) camera pan to the panel settles.

export const SPEC_TITLE = 'Waitlist Landing'
export const SPEC_LINES: Array<{ at: number; text: string }> = [
  { at: 10000, text: 'Hero with a one-line pitch' },
  { at: 10800, text: 'Email capture + “what would you build?”' },
  { at: 11600, text: 'Persist signups to the database' },
  { at: 12400, text: 'Validate email before accepting' },
]
export const SPEC_ACCEPTED_AT = 13000

// ── Kanban tab ───────────────────────────────────────────────────────────────

export const KANBAN_CARDS: KanbanCardScript[] = [
  {
    id: 'form',
    title: 'Waitlist form UI',
    moves: [[13400, 'backlog'], [14000, 'doing'], [15600, 'done']],
  },
  {
    id: 'api',
    title: 'POST /api/waitlist',
    moves: [[13400, 'backlog'], [14800, 'doing'], [16400, 'done']],
  },
  {
    id: 'validate',
    title: 'Email validation',
    moves: [[13400, 'backlog'], [16000, 'doing'], [18000, 'done']],
  },
]

// ── Tab switches ──────────────────────────────────────────────────────────────

const TAB_SWITCHES: Array<[number, AppTab]> = [
  [0, 'preview'],
  [8200, 'spec'],
  [13400, 'kanban'],
  [19000, 'preview'],
]

// ── Camera focus track ─────────────────────────────────────────────────────────

const FOCUS_TRACK: Array<[number, MovieFocus]> = [
  [0, 'overview'],
  [2100, 'chat'], // lean in as the request types itself
  [8200, 'app'], // pan to the spec / tasks / preview panel (slow, with dwell)
  [19200, 'chat'], // pan back to watch the agent test & self-correct
  [22600, 'app'], // settle on the live preview for the finale hand-off
]

// ── Captions (the "explaining parts") ─────────────────────────────────────────

const CAPTIONS: Array<[number, string]> = [
  [REPO_AT, 'Point GlennCode at any repo — or start fresh.'],
  [TYPE_START, 'Just describe what you want.'],
  [8200, 'It writes a spec first, so you both agree on the plan.'],
  [13400, 'Then it works the board — one task at a time.'],
  [19000, 'It runs your project in a sandbox and tests it itself.'],
  [19200, 'Short feedback loops: it catches its own bugs and fixes them.'],
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
  if (elapsed >= 22900) preview = 'live'
  else if (elapsed >= 19000) preview = 'building'

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
