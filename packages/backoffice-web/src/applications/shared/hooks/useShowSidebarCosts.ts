import { useCallback, useEffect, useState } from 'react'

// LocalStorage key for the workspace sidebar cost-visibility toggle. Pinned
// here (rather than threaded through a constants module) so the key — which
// participates in the user's *persisted* preferences surface area — is
// grep-friendly and version-able from one place.
const STORAGE_KEY = 'workspace.sidebar.showCosts'

/**
 * Persisted boolean that controls whether the workspace sidebar surfaces the
 * tiny per-workspace and per-project dollar amounts.
 *
 * <p>Defaults to {@code true} on first load — most users want the cost glance
 * — but defers to the persisted value the moment one exists, so the toggle
 * survives reloads. Wraps localStorage access in try/catch so private-mode /
 * SSR / disabled-storage shells degrade silently to "ephemeral default-on"
 * instead of throwing into render.</p>
 *
 * <p>Returns a tuple in the same shape as {@link useState} so call sites can
 * pattern-match it without any wrapper plumbing.</p>
 */
export function useShowSidebarCosts(): [boolean, (next: boolean) => void] {
  const [showCosts, setShowCostsState] = useState<boolean>(() => {
    try {
      const stored = localStorage.getItem(STORAGE_KEY)
      if (stored === null) return true
      return stored === 'true'
    } catch {
      return true
    }
  })

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, String(showCosts))
    } catch {
      // localStorage unavailable (private mode / quota / SSR) — silently
      // tolerate. The in-memory value still works for this session.
    }
  }, [showCosts])

  const setShowCosts = useCallback((next: boolean) => {
    setShowCostsState(next)
  }, [])

  return [showCosts, setShowCosts]
}
