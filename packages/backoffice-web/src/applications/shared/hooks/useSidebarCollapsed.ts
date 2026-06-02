import { useCallback, useEffect, useState } from 'react'

// LocalStorage key for the workspace sidebar collapsed state. Pinned here
// (rather than threaded through a constants module) so the key — which
// participates in the user's *persisted* preferences surface area — is
// grep-friendly and version-able from one place.
const STORAGE_KEY = 'workspace.sidebar.collapsed'

// Cross-component sync: a single browser tab may host multiple consumers of
// this hook (the layout reads it to size its wrapper, the sidebar reads it
// to switch render modes). Without coordination, clicking the collapse
// button inside the sidebar only flips the sidebar's own copy and the
// layout never resizes. A tiny in-module pub/sub keeps every consumer on
// the same value without reaching for a context provider — collapsed is a
// purely-presentational toggle, so global mutable state is acceptable.
type Listener = (next: boolean) => void
const listeners = new Set<Listener>()
let cachedValue: boolean | null = null

function readInitial(): boolean {
  if (cachedValue !== null) return cachedValue
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    cachedValue = stored === 'true'
  } catch {
    cachedValue = false
  }
  return cachedValue
}

function broadcast(next: boolean) {
  cachedValue = next
  for (const listener of listeners) listener(next)
}

/**
 * Persisted boolean controlling whether the workspace sidebar renders in its
 * compact 56px icon-rail mode (collapsed) or its full 256px navigator mode.
 *
 * <p>Uses in-tab cross-component synchronisation so the
 * {@link WorkspaceShellLayout} wrapper and the
 * {@link ProjectsBranchesSidebar} itself always agree on the current
 * value — flipping it from inside the sidebar reactively resizes the
 * layout column without waiting for a remount.</p>
 *
 * <p>Defaults to {@code false} (expanded) on first load so first-time users
 * see the full navigator; defers to the persisted value the moment one
 * exists so the toggle survives reloads.</p>
 */
export function useSidebarCollapsed(): [boolean, (next: boolean) => void] {
  const [collapsed, setCollapsedState] = useState<boolean>(readInitial)

  // Subscribe to broadcasts from other in-tab consumers.
  useEffect(() => {
    const listener: Listener = (next) => setCollapsedState(next)
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  }, [])

  // Persist when our local value changes (driven by a setCollapsed call
  // originating from this hook instance).
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, String(collapsed))
    } catch {
      // localStorage unavailable (private mode / quota / SSR) — silently
      // tolerate. The in-memory value still works for this session.
    }
  }, [collapsed])

  const setCollapsed = useCallback((next: boolean) => {
    setCollapsedState(next)
    broadcast(next)
  }, [])

  return [collapsed, setCollapsed]
}
