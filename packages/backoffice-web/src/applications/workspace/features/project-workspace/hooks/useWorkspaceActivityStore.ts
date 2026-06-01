import { useMemo, useSyncExternalStore } from 'react'

/**
 * One log entry shown in the workspace sidebar activity log. Each entry is
 * derived from a terminal session event (TurnCompleted / TurnFailed /
 * TurnCanceled) on a branch in the current workspace.
 *
 * <p>Entries store ONLY IDs — project + branch names are resolved at
 * render-time in {@link WorkspaceActivityLog} via TanStack Query so the labels
 * stay current if a project / branch is later renamed, and so we never paint
 * a stale "(unknown project)" string just because the workspace projects list
 * happened not to be in cache when the SignalR event arrived.</p>
 */
export interface WorkspaceActivityEntry {
  /** Stable key built from `${sessionId}:${sequence}` so the same terminal
   *  event arriving twice via reconnect dedupes cleanly. */
  id: string
  conversationId: string
  projectId: string | null
  branchId: string | null
  /** Display label for the user. */
  status: 'idle' | 'failed' | 'running'
  /** ms since epoch — used both for the 1h cutoff and for sort order. */
  timestamp: number
  unread: boolean
}

type Listener = () => void

interface State {
  entries: WorkspaceActivityEntry[]
}

// ── Module-scope store ──────────────────────────────────────────────────────
//
// We keep state in module scope so the activity log survives route changes
// within the workspace (only re-mounts of the consuming component drop it,
// and there's only one consumer per shell). Zustand isn't installed and a
// useSyncExternalStore-driven manual store is ~30 lines — pragmatic.

const STORAGE_KEY = 'workspaceActivityLog.v2'
const MAX_AGE_MS = 60 * 60 * 1000 // 1 hour cutoff
const MAX_ENTRIES = 50 // hard cap on what we keep in memory / storage

function loadInitial(): WorkspaceActivityEntry[] {
  if (typeof window === 'undefined') return []
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    const cutoff = Date.now() - MAX_AGE_MS
    return parsed
      .filter((e): e is WorkspaceActivityEntry => {
        if (!e || typeof e !== 'object') return false
        const obj = e as Record<string, unknown>
        return (
          typeof obj.id === 'string' &&
          typeof obj.conversationId === 'string' &&
          typeof obj.status === 'string' &&
          typeof obj.timestamp === 'number' &&
          typeof obj.unread === 'boolean'
        )
      })
      .filter((e) => e.timestamp >= cutoff)
      .slice(0, MAX_ENTRIES)
  } catch {
    return []
  }
}

let state: State = { entries: loadInitial() }
const listeners = new Set<Listener>()

function persist() {
  if (typeof window === 'undefined') return
  try {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(state.entries))
  } catch {
    // best-effort — storage quota or private mode
  }
}

function emit() {
  for (const l of listeners) l()
}

function subscribe(listener: Listener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

function getSnapshot(): WorkspaceActivityEntry[] {
  return state.entries
}

/**
 * Push a new terminal-session entry.
 *
 * <p>Dedupe semantics — the log shows at most ONE entry per branch:
 * <ul>
 *   <li>If an entry already exists for the incoming {@code branchId}, it is
 *       removed first so the new event lands at the top of the list (most
 *       recent wins).</li>
 *   <li>Idempotency on {@code id} is preserved as a special case: if the
 *       exact same {@code sessionId:sequence} arrives twice (e.g. SignalR
 *       reconnect replay), we merge rather than churn — same status, same
 *       branch, no reason to flip unread back on.</li>
 *   <li>If the previous entry on the same branch was already read but a
 *       fresh terminal event arrives (new {@code id}), the new entry comes
 *       in as unread again — the user wants to know something new
 *       happened.</li>
 *   <li>{@code timestamp} is always taken from the incoming event so the
 *       1h cutoff slides correctly.</li>
 * </ul></p>
 */
export function pushActivity(entry: WorkspaceActivityEntry): void {
  // ── Exact-id idempotency (reconnect replay) ─────────────────────────────
  const sameIdIndex = state.entries.findIndex((e) => e.id === entry.id)
  if (sameIdIndex >= 0) {
    const prev = state.entries[sameIdIndex]
    const merged: WorkspaceActivityEntry = {
      ...entry,
      // Don't flip a read entry back to unread on replay.
      unread: prev.unread === false ? false : entry.unread,
    }
    const next = [...state.entries]
    next[sameIdIndex] = merged
    state = { entries: next }
    persist()
    emit()
    return
  }

  // ── Per-branch dedupe ───────────────────────────────────────────────────
  // Remove any prior entries for the same branch — only the most recent
  // event per branch is kept in the log. {@code null} branchIds (degraded
  // fallback path) are NOT deduped against each other since they don't
  // identify a branch; they just stack normally and the MAX_ENTRIES cap
  // protects us.
  const filtered =
    entry.branchId !== null
      ? state.entries.filter((e) => e.branchId !== entry.branchId)
      : state.entries

  const next = [entry, ...filtered].slice(0, MAX_ENTRIES)
  state = { entries: next }
  persist()
  emit()
}

export function markRead(id: string): void {
  let mutated = false
  const next = state.entries.map((e) => {
    if (e.id !== id) return e
    if (e.unread === false) return e
    mutated = true
    return { ...e, unread: false }
  })
  if (!mutated) return
  state = { entries: next }
  persist()
  emit()
}

/**
 * Mark every entry pointing at {@code branchId} as read. Called when the
 * user navigates to a branch — the act of opening the branch is the implicit
 * "I've seen what happened here" gesture, so the entries for that branch
 * stop counting as unread immediately and disappear from the inbox-style
 * filtered Activity panel.
 *
 * <p>No-op when no matching entries exist or all matches are already read,
 * so it's safe to call from a route-change effect that fires on every
 * branch navigation.</p>
 */
export function markBranchRead(branchId: string): void {
  let mutated = false
  const next = state.entries.map((e) => {
    if (e.branchId !== branchId) return e
    if (e.unread === false) return e
    mutated = true
    return { ...e, unread: false }
  })
  if (!mutated) return
  state = { entries: next }
  persist()
  emit()
}

export function markAllRead(): void {
  let mutated = false
  const next = state.entries.map((e) => {
    if (!e.unread) return e
    mutated = true
    return { ...e, unread: false }
  })
  if (!mutated) return
  state = { entries: next }
  persist()
  emit()
}

/**
 * Remove entries that match {@code predicate}. Used to drop cross-workspace
 * stale entries when the current workspace's projects list settles — anything
 * pointing at a projectId not in the workspace can never resolve and must
 * not flash "Borttaget projekt" on first render of the new workspace.
 *
 * <p>No-op when nothing matches, so it's cheap to call from a useEffect that
 * re-runs on every projects-query settle.</p>
 */
export function purgeActivityWhere(
  predicate: (entry: WorkspaceActivityEntry) => boolean,
): void {
  const next = state.entries.filter((e) => !predicate(e))
  if (next.length === state.entries.length) return
  state = { entries: next }
  persist()
  emit()
}

/**
 * Hook for components that want to render the activity log. Returns the
 * full raw entry list — call sites apply their own age/limit filter at
 * render time so age-based hiding is reactive without a tick.
 */
export function useWorkspaceActivity(): WorkspaceActivityEntry[] {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
}

/**
 * Selector hook used by sidebar branch rows to render an inline unread
 * affordance — returns the status of THIS branch's most-recent unread
 * activity entry, or {@code null} when there is none.
 *
 * <p>Surfaces the entry status (idle / failed / running) so the dot can
 * convey whether the branch finished cleanly or failed, matching the dot
 * palette used by the bottom Activity panel. Returns {@code null} when no
 * unread entry exists for the branch — the row then falls back to its
 * normal (in-flight pulse OR no-dot) presentation.</p>
 *
 * <p>Subscribes to the same module store as {@link useWorkspaceActivity};
 * each branch row owns one subscription. Re-renders happen on every store
 * mutation, but the work per row is an O(n_entries) lookup against a
 * 50-cap array, which is negligible.</p>
 */
export function useBranchUnreadActivityStatus(
  branchId: string | null,
): 'idle' | 'failed' | 'running' | null {
  const entries = useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
  return useMemo(() => {
    if (!branchId) return null
    const entry = entries.find((e) => e.branchId === branchId && e.unread)
    return entry?.status ?? null
  }, [entries, branchId])
}

/** Re-exported for tests / debug consoles. */
export const __workspaceActivityInternals = {
  MAX_AGE_MS,
  MAX_ENTRIES,
}
