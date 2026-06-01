import { useCallback, useEffect, useState } from 'react'

/**
 * localStorage key under which the per-conversation "yolo" mode flag is
 * persisted. When yolo is on, the agent is allowed to invoke tools without
 * prompting the user for permission each time — when off, the agent pauses
 * and waits for explicit approval before any tool use.
 *
 * <p>Scope is per-conversation (same scope as the agent model override) so
 * the toggle near the model picker behaves consistently: flipping it once
 * in this chat sticks for this chat and is independent of any other chat in
 * the project. Closing the tab and coming back preserves the choice;
 * clearing localStorage falls back to the default (on).</p>
 */
export function yoloModeKey(conversationId: string): string {
  return `glenn.yolo.${conversationId}`
}

/**
 * Default state when no value has been persisted yet. Yolo is on by default
 * so the chat composer feels fast and uninterrupted out of the box — users
 * can opt into permission prompts explicitly if they want guarded tool use.
 */
const DEFAULT_YOLO = true

/**
 * Read-only reader for the per-conversation yolo flag — used by the chat
 * submit path that only needs the current value at send time and doesn't
 * want to subscribe to localStorage events. Returns the default ({@code true})
 * when no value is stored, the {@code conversationId} is missing, or the
 * {@code localStorage} API is unavailable (private mode, SSR).
 *
 * <p>Mirrors {@code readAgentModelOverride} in spirit — both helpers let
 * the submit handler grab the latest user choice at the exact instant the
 * payload is built, without forcing the caller to depend on a React hook.</p>
 */
export function readYoloMode(
  conversationId: string | null | undefined,
): boolean {
  if (!conversationId) return DEFAULT_YOLO
  if (typeof window === 'undefined') return DEFAULT_YOLO
  try {
    const raw = window.localStorage.getItem(yoloModeKey(conversationId))
    if (raw === null) return DEFAULT_YOLO
    // Stored as "true" / "false" string — anything else falls back to the
    // default so a stale or corrupted key doesn't accidentally pin the
    // toggle to a permissive state the user can't see.
    if (raw === 'true') return true
    if (raw === 'false') return false
    return DEFAULT_YOLO
  } catch {
    return DEFAULT_YOLO
  }
}

/**
 * Reactive hook for the per-conversation yolo flag. Returns the current
 * boolean (defaulting to {@code true} when no value is stored) and a setter
 * that writes through to localStorage.
 *
 * <p>Listens for cross-tab updates via the native {@code storage} event so
 * two tabs against the same conversation stay in sync. Same-tab writes go
 * through the setter and update React state directly — the {@code storage}
 * event doesn't fire on the tab that wrote, which is why both paths
 * coexist.</p>
 */
export function useYoloMode(conversationId: string | null) {
  const [yolo, setYoloState] = useState<boolean>(() =>
    readYoloMode(conversationId),
  )

  // Re-read whenever the conversation id flips — the route changed to a
  // different conversation, so the toggle should reflect that chat's choice.
  useEffect(() => {
    setYoloState(readYoloMode(conversationId))
  }, [conversationId])

  // Cross-tab sync — listen for native storage events. Same-tab writes are
  // updated synchronously below in {@code setYolo}.
  useEffect(() => {
    if (!conversationId) return
    if (typeof window === 'undefined') return
    const key = yoloModeKey(conversationId)
    const handler = (e: StorageEvent) => {
      if (e.key !== key) return
      if (e.newValue === 'true') setYoloState(true)
      else if (e.newValue === 'false') setYoloState(false)
      else setYoloState(DEFAULT_YOLO)
    }
    window.addEventListener('storage', handler)
    return () => window.removeEventListener('storage', handler)
  }, [conversationId])

  const setYolo = useCallback(
    (next: boolean) => {
      if (!conversationId) {
        setYoloState(next)
        return
      }
      if (typeof window === 'undefined') {
        setYoloState(next)
        return
      }
      try {
        window.localStorage.setItem(
          yoloModeKey(conversationId),
          next ? 'true' : 'false',
        )
        setYoloState(next)
      } catch {
        // localStorage may throw in private mode or with quota issues. The
        // toggle still reflects the in-memory state for the rest of the
        // session — we just don't persist across reloads.
        setYoloState(next)
      }
    },
    [conversationId],
  )

  return { yolo, setYolo } as const
}
