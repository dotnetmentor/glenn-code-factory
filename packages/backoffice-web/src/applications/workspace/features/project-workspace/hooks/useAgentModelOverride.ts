import { useCallback, useEffect, useState } from 'react'

/**
 * localStorage key under which a per-conversation agent model override is
 * persisted. The override is sticky for the lifetime of the conversation: the
 * user can flip the dropdown in {@code ChatChrome} once and every subsequent
 * send for that conversation carries the chosen model id through the
 * {@code submitPrompt} payload until they change it again or clear it.
 *
 * <p>The override is intentionally per-conversation rather than per-session
 * or per-branch — switching branches creates a new conversation, and a
 * conversation is the unit a user reads as "my chat" mentally. Storing it
 * under the conversation id means closing the tab and coming back keeps the
 * choice; clearing localStorage resets it to the project default.</p>
 */
export function agentModelOverrideKey(conversationId: string): string {
  return `agent-model-override:${conversationId}`
}

/**
 * Read-only reader for the override — used by callers (such as the chat send
 * mutation) that only need the current value at submit time and don't want to
 * subscribe to localStorage events. Falls back to {@code null} when the key is
 * missing or the {@code localStorage} API is unavailable (private mode quirks,
 * SSR builds).
 */
export function readAgentModelOverride(
  conversationId: string | null | undefined,
): string | null {
  if (!conversationId) return null
  if (typeof window === 'undefined') return null
  try {
    const value = window.localStorage.getItem(
      agentModelOverrideKey(conversationId),
    )
    return value && value.trim().length > 0 ? value : null
  } catch {
    return null
  }
}

/**
 * Reactive hook for the per-conversation override. Returns the current value
 * (or {@code null} when none is set) and two setters: one to assign a new id,
 * one to clear the entry entirely.
 *
 * <p>Listens for cross-tab updates via the native {@code storage} event so
 * two tabs against the same conversation stay in sync. Same-tab writes go
 * through the setter and update React state directly — the {@code storage}
 * event doesn't fire on the tab that wrote, which is why we update both.</p>
 */
export function useAgentModelOverride(conversationId: string | null) {
  const [value, setValue] = useState<string | null>(() =>
    readAgentModelOverride(conversationId),
  )

  // Re-read whenever the conversation id flips — the parent route's URL
  // changes mean a different conversation is now in focus.
  useEffect(() => {
    setValue(readAgentModelOverride(conversationId))
  }, [conversationId])

  // Cross-tab sync — listen for native storage events. Same-tab writes are
  // updated synchronously below in {@code setOverride} / {@code clearOverride}.
  useEffect(() => {
    if (!conversationId) return
    if (typeof window === 'undefined') return
    const key = agentModelOverrideKey(conversationId)
    const handler = (e: StorageEvent) => {
      if (e.key !== key) return
      setValue(
        e.newValue && e.newValue.trim().length > 0 ? e.newValue : null,
      )
    }
    window.addEventListener('storage', handler)
    return () => window.removeEventListener('storage', handler)
  }, [conversationId])

  const setOverride = useCallback(
    (next: string | null) => {
      if (!conversationId) return
      if (typeof window === 'undefined') return
      try {
        const key = agentModelOverrideKey(conversationId)
        if (next === null) {
          window.localStorage.removeItem(key)
        } else {
          window.localStorage.setItem(key, next)
        }
        setValue(next)
      } catch {
        // localStorage may throw in private mode or with quota issues. The
        // dropdown still reflects the in-memory state for the rest of the
        // session — we just don't persist across reloads.
        setValue(next)
      }
    },
    [conversationId],
  )

  return { value, setOverride } as const
}
