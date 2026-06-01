import { useEffect } from 'react'

/**
 * Sets {@code document.title} for the lifetime of the calling component and
 * restores the previous title on unmount.
 *
 * <p>Used by the workspace shell's calm canvas routes (landing, settings,
 * new-session) so browser tabs read e.g. {@code "Welcome back · acme ·
 * GlennCode"} instead of the static fallback. Per-branch IDE routes have
 * their own {@code useProjectTabTitle} hook that bakes in runtime state
 * ({@code "agent-template — sleeping"}) and SignalR-driven badge dots — this
 * hook is intentionally simpler and doesn't try to compete with that.</p>
 *
 * <p>Pass {@code null}, {@code undefined}, or an empty string to leave the
 * document title alone — useful when the title depends on a query that
 * hasn't resolved yet ({@code useDocumentTitle(workspace?.name && `…`)}).</p>
 *
 * <p>The previous title is captured the first time the effect runs and
 * restored on cleanup, so nested mounts (e.g. a drawer over a page) cooperate
 * cleanly — the inner one wins while open, the outer one comes back when it
 * unmounts. This relies on effects running in mount order, which React
 * guarantees.</p>
 */
export function useDocumentTitle(title: string | null | undefined): void {
  useEffect(() => {
    if (!title) return
    const previous = document.title
    document.title = title
    return () => {
      document.title = previous
    }
  }, [title])
}
