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
 * centers on whichever panel the system is acting on.
 */

export type AppTab = 'services' | 'chat' | 'spec' | 'kanban' | 'preview'

export type ChatRole = 'user' | 'assistant'

export interface ChatBubble {
  kind: 'bubble'
  at: number
  role: ChatRole
  /** Plain text; the user bubble is revealed with a typewriter. */
  text: string
}

export interface ChatTool {
  kind: 'tool'
  at: number
  label: string
  detail: string
  tone?: 'default' | 'warn' | 'success'
}

export type ChatItem = ChatBubble | ChatTool

export type KanbanColumn = 'backlog' | 'doing' | 'done'

export interface KanbanCardScript {
  id: string
  title: string
  moves: Array<[number, KanbanColumn]>
}

/** A detected sandbox service that boots, then comes online. */
export interface ServiceScript {
  id: string
  label: string
  /** Mono detail line, e.g. "dotnet · :5338". */
  detail: string
  /** When it flips from booting → online. */
  at: number
}

/** Which panel the camera centers + enlarges. */
export type MovieFocus = 'overview' | 'chat' | 'app'

/** The line the visitor watches type itself in. */
export const USER_MESSAGE = 'Build me a waitlist landing page for my new app.'

/**
 * Cinematic display serif for the landing's narration subtitles + finale
 * headline (Instrument Serif, loaded in index.html). Falls back to a system
 * serif so the cinematic treatment survives even where the CDN is blocked.
 */
export const CINEMATIC_SERIF =
  "'Instrument Serif', 'Fraunces', Georgia, 'Times New Roman', serif"

// ── Timeline constants (ms) ──────────────────────────────────────────────────

const REPO_AT = 1000
const TYPE_START = 2600
const TYPE_END = 5800
export const FINALE_AT = 30800

// ── Chat transcript ──────────────────────────────────────────────────────────

export const CHAT_ITEMS: ChatItem[] = [
  { kind: 'bubble', at: TYPE_START, role: 'user', text: USER_MESSAGE },
  { kind: 'bubble', at: 6400, role: 'assistant', text: 'Let me look at your repo first…' },
  { kind: 'tool', at: 7000, label: 'Analyzing repository', detail: 'detecting stack', tone: 'default' },
  {
    kind: 'bubble',
    at: 8400,
    role: 'assistant',
    text: 'Looks like a .NET API, a React frontend, and Postgres. I’ll spin those up in the sandbox.',
  },
  { kind: 'bubble', at: 11900, role: 'assistant', text: 'Sandbox is up and running. Let’s build. 🚀' },
  { kind: 'tool', at: 13000, label: 'Writing spec', detail: 'Waitlist Landing', tone: 'default' },
  { kind: 'bubble', at: 17600, role: 'assistant', text: 'Spec accepted — breaking it into tasks.' },
  { kind: 'tool', at: 18300, label: 'WaitlistForm.tsx', detail: 'email + submit', tone: 'default' },
  { kind: 'tool', at: 19300, label: 'POST /api/waitlist', detail: 'persist signup', tone: 'default' },
  { kind: 'bubble', at: 23200, role: 'assistant', text: 'Running it in the sandbox to check it works…' },
  { kind: 'tool', at: 24400, label: 'Preview', detail: 'empty email slipped through', tone: 'warn' },
  { kind: 'tool', at: 26000, label: 'Fix', detail: 'added email validation', tone: 'success' },
  { kind: 'bubble', at: 27200, role: 'assistant', text: 'Fixed and verified. Here it is — live. Try it 👇' },
]

// ── Services tab (the runtime-spec beat) ───────────────────────────────────────

export const SERVICES: ServiceScript[] = [
  { id: 'api', label: '.NET API', detail: 'dotnet · :5338', at: 9400 },
  { id: 'web', label: 'React (Vite)', detail: 'node · :5173', at: 10300 },
  { id: 'db', label: 'Postgres', detail: 'database · :5432', at: 11200 },
]

// ── Spec tab ───────────────────────────────────────────────────────────────

export const SPEC_TITLE = 'Waitlist Landing'
export const SPEC_LINES: Array<{ at: number; text: string }> = [
  { at: 14200, text: 'Hero with a one-line pitch' },
  { at: 15000, text: 'Email capture + “what would you build?”' },
  { at: 15800, text: 'Persist signups to the database' },
  { at: 16600, text: 'Validate email before accepting' },
]
export const SPEC_ACCEPTED_AT = 17300

// ── Kanban tab ───────────────────────────────────────────────────────────────

export const KANBAN_CARDS: KanbanCardScript[] = [
  { id: 'form', title: 'Waitlist form UI', moves: [[18200, 'backlog'], [18800, 'doing'], [20400, 'done']] },
  { id: 'api', title: 'POST /api/waitlist', moves: [[18200, 'backlog'], [19600, 'doing'], [21200, 'done']] },
  { id: 'validate', title: 'Email validation', moves: [[18200, 'backlog'], [20800, 'doing'], [22800, 'done']] },
]

// ── Tab switches ──────────────────────────────────────────────────────────────

const TAB_SWITCHES: Array<[number, AppTab]> = [
  [0, 'preview'],
  [8700, 'services'],
  [13400, 'spec'],
  [18200, 'kanban'],
  [23400, 'preview'],
]

// ── Camera focus track ─────────────────────────────────────────────────────────

const FOCUS_TRACK: Array<[number, MovieFocus]> = [
  [0, 'overview'],
  [2000, 'chat'], // lean in as the request types + repo analysis
  [8700, 'app'], // pan to watch services boot, then spec / tasks
  [23600, 'chat'], // pan back to watch the agent test & self-correct
  [27600, 'app'], // settle on the live preview for the finale
]

// ── Captions (the "explaining parts") ─────────────────────────────────────────

const CAPTIONS: Array<[number, string]> = [
  [REPO_AT, 'Point GlennCode at any repo — or start fresh.'],
  [TYPE_START, 'Just describe what you want.'],
  [7000, 'First it reads your repo to learn the stack.'],
  [8700, 'It detects your services and spins them up in a sandbox.'],
  [13400, 'Then it writes a spec, so you both agree on the plan.'],
  [18200, 'It works the board — one task at a time.'],
  [23400, 'It runs your project in the sandbox and tests it itself.'],
  [23600, 'Short feedback loops: it catches its own bugs and fixes them.'],
  [FINALE_AT, 'This waitlist is real — built live, running in a sandbox. Sign up right inside it.'],
]

// ── Preview phases ─────────────────────────────────────────────────────────────

export type PreviewPhase = 'idle' | 'building' | 'live'

// ── Derived state ──────────────────────────────────────────────────────────────

export interface ServiceState {
  id: string
  label: string
  detail: string
  online: boolean
}

export interface MovieState {
  repoConnected: boolean
  typedUser: string
  userTyping: boolean
  chat: ChatItem[]
  activeTab: AppTab
  services: ServiceState[]
  specLines: string[]
  specAccepted: boolean
  kanban: Record<string, KanbanColumn>
  preview: PreviewPhase
  caption: string
  focus: MovieFocus
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

  const services: ServiceState[] = SERVICES.map((s) => ({
    id: s.id,
    label: s.label,
    detail: s.detail,
    online: elapsed >= s.at,
  }))

  const specLines = SPEC_LINES.filter((l) => elapsed >= l.at).map((l) => l.text)
  const specAccepted = elapsed >= SPEC_ACCEPTED_AT

  const kanban: Record<string, KanbanColumn> = {}
  for (const card of KANBAN_CARDS) {
    kanban[card.id] = lastBefore(card.moves, elapsed, 'backlog')
  }

  let preview: PreviewPhase = 'idle'
  if (elapsed >= 27300) preview = 'live'
  else if (elapsed >= 23400) preview = 'building'

  const caption = lastBefore(CAPTIONS, elapsed, CAPTIONS[0][1])
  const focus = lastBefore(FOCUS_TRACK, elapsed, 'overview')

  return {
    repoConnected: elapsed >= REPO_AT,
    typedUser,
    userTyping,
    chat,
    activeTab,
    services,
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
