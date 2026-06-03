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
  detail: string
  at: number
}

/** Which panel the camera centers + enlarges. */
export type MovieFocus = 'overview' | 'chat' | 'app'

/** The line the visitor watches type itself in. */
export const USER_MESSAGE = 'Build me a waitlist landing page for my new app.'

/** The agent model the demo picks in its opening beat (powered by the Cursor SDK). */
export const MODEL_NAME = 'Composer 2.5'

/**
 * Cinematic display serif for the landing's narration subtitles + finale
 * headline (Instrument Serif, loaded in index.html). Falls back to a system
 * serif so the cinematic treatment survives even where the CDN is blocked.
 */
export const CINEMATIC_SERIF =
  "'Instrument Serif', 'Fraunces', Georgia, 'Times New Roman', serif"

// ── Timeline constants (ms) ──────────────────────────────────────────────────

const REPO_AT = 300
const MODEL_MENU_OPEN = 500
const MODEL_MENU_CLOSE = 2900
const TYPE_START = 4200
const TYPE_END = 7200
export const FINALE_AT = 35400

// ── Chat transcript ──────────────────────────────────────────────────────────

export const CHAT_ITEMS: ChatItem[] = [
  { kind: 'bubble', at: TYPE_START, role: 'user', text: USER_MESSAGE },
  { kind: 'bubble', at: 7800, role: 'assistant', text: 'Let me look at your repo first…' },
  { kind: 'tool', at: 8600, label: 'Analyzing repository', detail: 'detecting stack', tone: 'default' },
  {
    kind: 'bubble',
    at: 10000,
    role: 'assistant',
    text: 'Looks like a .NET API, a React frontend, and Postgres. I’ll spin those up in the sandbox.',
  },
  { kind: 'bubble', at: 16200, role: 'assistant', text: 'Sandbox is up and running. Let’s build. 🚀' },
  { kind: 'tool', at: 17200, label: 'Writing spec', detail: 'Waitlist Landing', tone: 'default' },
  { kind: 'bubble', at: 22100, role: 'assistant', text: 'Spec accepted — breaking it into tasks.' },
  { kind: 'tool', at: 22800, label: 'WaitlistForm.tsx', detail: 'email + submit', tone: 'default' },
  { kind: 'tool', at: 23800, label: 'POST /api/waitlist', detail: 'persist signup', tone: 'default' },
  { kind: 'bubble', at: 27800, role: 'assistant', text: 'Running it in the sandbox to check it works…' },
  { kind: 'tool', at: 29000, label: 'Preview', detail: 'empty email slipped through', tone: 'warn' },
  { kind: 'tool', at: 30600, label: 'Fix', detail: 'added email validation', tone: 'success' },
  { kind: 'bubble', at: 31800, role: 'assistant', text: 'Fixed and verified. Here it is — live. Try it 👇' },
]

// ── Services tab (runtime-spec beat — Fly.io sandbox) ─────────────────────────

export const SERVICES: ServiceScript[] = [
  { id: 'api', label: '.NET API', detail: 'dotnet · :5338', at: 13200 },
  { id: 'web', label: 'React (Vite)', detail: 'node · :5173', at: 14400 },
  { id: 'db', label: 'Postgres', detail: 'database · :5432', at: 15600 },
]

// ── Spec tab ───────────────────────────────────────────────────────────────

export const SPEC_TITLE = 'Waitlist Landing'
export const SPEC_LINES: Array<{ at: number; text: string }> = [
  { at: 18200, text: 'Hero with a one-line pitch' },
  { at: 19000, text: 'Email capture + “what would you build?”' },
  { at: 19800, text: 'Persist signups to the database' },
  { at: 20600, text: 'Validate email before accepting' },
]
export const SPEC_ACCEPTED_AT = 21400

// ── Kanban tab ───────────────────────────────────────────────────────────────

export const KANBAN_CARDS: KanbanCardScript[] = [
  { id: 'form', title: 'Waitlist form UI', moves: [[22400, 'backlog'], [23000, 'doing'], [24600, 'done']] },
  { id: 'api', title: 'POST /api/waitlist', moves: [[22400, 'backlog'], [23800, 'doing'], [25400, 'done']] },
  { id: 'validate', title: 'Email validation', moves: [[22400, 'backlog'], [25000, 'doing'], [27000, 'done']] },
]

// ── Tab switches ──────────────────────────────────────────────────────────────

const TAB_SWITCHES: Array<[number, AppTab]> = [
  [0, 'preview'],
  [12000, 'services'],
  [17400, 'spec'],
  [22200, 'kanban'],
  [27800, 'preview'],
]

// ── Camera focus track ─────────────────────────────────────────────────────────

const FOCUS_TRACK: Array<[number, MovieFocus]> = [
  [0, 'overview'], // opening: pick the model, read "Built on the Cursor SDK"
  [4200, 'chat'], // lean in as the request types + repo analysis
  [12000, 'app'], // pan to watch services boot on Fly.io, then spec / tasks
  [28000, 'chat'], // pan back to watch the agent test & self-correct
  [32000, 'app'], // settle on the live preview for the finale
]

// ── Captions (the "explaining parts") ─────────────────────────────────────────

const CAPTIONS: Array<[number, string]> = [
  [300, 'Built on the Cursor SDK.'],
  [TYPE_START, 'Point it at any repo, then just describe what you want.'],
  [8600, 'First it reads your repo to learn the stack.'],
  [12000, 'It spins the services up on Fly.io — fresh infra in seconds.'],
  [17400, 'Then it writes a spec, so you both agree on the plan.'],
  [22200, 'It works the board — one task at a time.'],
  [27800, 'It runs your project in the sandbox and tests it itself.'],
  [28000, 'Short feedback loops: it catches its own bugs and fixes them.'],
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
  /** Opening beat: the model picker menu is open while the demo "selects" the model. */
  modelMenuOpen: boolean
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
  if (elapsed >= 31900) preview = 'live'
  else if (elapsed >= 27800) preview = 'building'

  const caption = lastBefore(CAPTIONS, elapsed, CAPTIONS[0][1])
  const focus = lastBefore(FOCUS_TRACK, elapsed, 'overview')

  return {
    repoConnected: elapsed >= REPO_AT,
    modelMenuOpen: elapsed >= MODEL_MENU_OPEN && elapsed < MODEL_MENU_CLOSE,
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
