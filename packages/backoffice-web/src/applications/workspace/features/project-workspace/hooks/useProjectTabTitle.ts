import { useEffect, useRef, useState } from 'react'
import { RuntimeState } from '../../../../../api/queries-commands'
import type { AgentHubConnection } from '../../../../../lib/signalr'

/**
 * Per-tab, per-project live {@code document.title} writer for the project
 * workspace route. Mirrors GitHub's tab-title pattern: name + status, with a
 * leading {@code ●} bullet when a noteworthy state transition happened while
 * the tab was blurred (cleared the moment the tab regains focus).
 *
 * <p>Strictly scoped to the workspace surface — the hook captures the
 * previous {@code document.title} on first mount and restores it on cleanup
 * (or when {@code projectId} changes), so leaving the route does not bleed a
 * stale title into other pages (projects list, members, settings, etc.).</p>
 *
 * <p>State lives entirely inside the hook; consumers see {@code void}. Only
 * the title side-effect runs on changes, so the parent route does not
 * re-render on every keystroke event.</p>
 *
 * <p>Cross-project safety: the {@code RuntimeStateChanged} listener filters
 * on {@code payload.projectId === projectId}. The {@code AgentEvent}
 * notification shape does not carry a {@code projectId} field, but the
 * {@link AgentHubConnection} is constructed against a single project group
 * on the server side, so every event delivered on this connection already
 * belongs to the current project.</p>
 */
export function useProjectTabTitle(params: {
  projectId: string
  projectName: string | undefined
  runtimeState: RuntimeState | string | undefined | null
  connection: AgentHubConnection | null
}): void {
  const { projectId, projectName, runtimeState, connection } = params

  // ── tab visibility ───────────────────────────────────────────────────────
  // Read synchronously on init so the very first title write knows whether
  // to badge. SSR guard via {@code typeof document}.
  const [isVisible, setIsVisible] = useState<boolean>(() =>
    typeof document === 'undefined'
      ? true
      : document.visibilityState === 'visible',
  )

  useEffect(() => {
    if (typeof document === 'undefined') return
    const onVis = () => {
      setIsVisible(document.visibilityState === 'visible')
    }
    document.addEventListener('visibilitychange', onVis)
    return () => {
      document.removeEventListener('visibilitychange', onVis)
    }
  }, [])

  // ── active turn state ────────────────────────────────────────────────────
  // No history preload — first mount assumes idle. If the user lands on a
  // project that's actively running, the next event flips this. A perfect
  // read across page reload is a non-goal for v1.
  const [turnState, setTurnState] = useState<'idle' | 'running'>('idle')

  // Reset turn state when the project changes — switching projects in the
  // same tab should not carry over a stale running indicator.
  useEffect(() => {
    setTurnState('idle')
  }, [projectId])

  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onAgentEvent((evt) => {
      // Card 4 (cursor-native-chat-ux): the wire payload now nests the
      // polymorphic event under {@code evt.event} with a {@code eventKind}
      // discriminator. We narrow on {@code 'status'} and read the inner
      // {@code status} field which mirrors the old TurnStarted/Completed
      // taxonomy via the {@code AgentEventRunStatus} enum.
      const inner = evt.event as { eventKind?: string; status?: string }
      if (inner.eventKind !== 'status') return
      switch (inner.status) {
        case 'Running':
          setTurnState('running')
          break
        case 'Finished':
        case 'Error':
        case 'Cancelled':
        case 'Expired':
          setTurnState('idle')
          break
        default:
          // Creating + other transient states — keep current.
          break
      }
    })
    return () => {
      unsubscribe()
    }
  }, [connection, projectId])

  // ── unseen badge ─────────────────────────────────────────────────────────
  // Set true on a noteworthy event arriving while the tab is hidden. Cleared
  // on visibility flip back to visible. We keep a ref mirror of {@code
  // isVisible} so the event listeners (which close over the initial state)
  // see the up-to-date value without restarting the subscription on every
  // visibility flip.
  const [hasUnseen, setHasUnseen] = useState<boolean>(false)
  const isVisibleRef = useRef<boolean>(isVisible)
  useEffect(() => {
    isVisibleRef.current = isVisible
    if (isVisible) setHasUnseen(false)
  }, [isVisible])

  // Reset unseen when project changes — a badge from project A should not
  // follow the user to project B.
  useEffect(() => {
    setHasUnseen(false)
  }, [projectId])

  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onAgentEvent((evt) => {
      if (isVisibleRef.current) return
      const inner = evt.event as { eventKind?: string; status?: string }
      if (inner.eventKind !== 'status') return
      if (
        inner.status === 'Finished' ||
        inner.status === 'Error' ||
        inner.status === 'Cancelled' ||
        inner.status === 'Expired'
      ) {
        setHasUnseen(true)
      }
    })
    return () => {
      unsubscribe()
    }
  }, [connection, projectId])

  // Track last-seen runtime state so we can detect "Online (from any warming
  // state)" — a meaningful transition for the badge while hidden.
  const prevRuntimeRef = useRef<RuntimeState | string | null | undefined>(
    runtimeState,
  )
  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onRuntimeStateChanged((payload) => {
      if (payload.projectId !== projectId) return
      if (isVisibleRef.current) return
      const next = payload.toState
      const prev = prevRuntimeRef.current
      const meaningful =
        next === RuntimeState.Failed ||
        next === RuntimeState.Crashed ||
        next === RuntimeState.Suspended ||
        (next === RuntimeState.Online &&
          (prev === RuntimeState.Booting ||
            prev === RuntimeState.Bootstrapping ||
            prev === RuntimeState.Waking ||
            prev === RuntimeState.Pending))
      if (meaningful) setHasUnseen(true)
    })
    return () => {
      unsubscribe()
    }
  }, [connection, projectId])

  // Keep the runtime-state ref current for the meaningful-transition check.
  useEffect(() => {
    prevRuntimeRef.current = runtimeState
  }, [runtimeState])

  // ── title composition + restore ─────────────────────────────────────────
  // Snapshot the original title ONCE per mount/projectId, guarded by a ref
  // so React strict-mode double-effects don't snapshot the title we just
  // wrote. On cleanup, restore it. Same approach for the favicon href so
  // leaving the route doesn't bleed an unseen badge into other pages.
  const originalTitleRef = useRef<string | null>(null)
  const originalFaviconHrefRef = useRef<string | null>(null)
  useEffect(() => {
    if (typeof document === 'undefined') return
    if (originalTitleRef.current === null) {
      originalTitleRef.current = document.title
    }
    const faviconLink = document.getElementById(
      'favicon',
    ) as HTMLLinkElement | null
    if (faviconLink && originalFaviconHrefRef.current === null) {
      originalFaviconHrefRef.current = faviconLink.href
    }
    return () => {
      if (originalTitleRef.current !== null) {
        document.title = originalTitleRef.current
        originalTitleRef.current = null
      }
      if (originalFaviconHrefRef.current !== null) {
        const link = document.getElementById(
          'favicon',
        ) as HTMLLinkElement | null
        if (link) {
          link.href = originalFaviconHrefRef.current
        }
        originalFaviconHrefRef.current = null
      }
    }
    // Restore + re-capture if projectId changes (e.g. user navigates between
    // two workspace URLs without unmounting the route).
  }, [projectId])

  useEffect(() => {
    if (typeof document === 'undefined') return

    let next: string
    if (!projectName) {
      next = 'Loading…'
    } else {
      const statusLabel = deriveStatusLabel(runtimeState, turnState)
      next = statusLabel ? `${projectName} — ${statusLabel}` : projectName
    }
    const showBadge = hasUnseen && !isVisible
    if (showBadge) {
      next = `● ${next}`
    }
    document.title = next

    const link = document.getElementById(
      'favicon',
    ) as HTMLLinkElement | null
    if (link) {
      link.href = showBadge ? '/favicon-unseen.svg' : '/favicon.svg'
    }
  }, [projectName, runtimeState, turnState, hasUnseen, isVisible])
}

/**
 * Map (runtimeState, turnState) → human status label. Returns {@code null}
 * for unknown states so the caller can fall back to just the project name.
 */
function deriveStatusLabel(
  runtimeState: RuntimeState | string | undefined | null,
  turnState: 'idle' | 'running',
): string | null {
  switch (runtimeState) {
    case RuntimeState.Failed:
    case RuntimeState.Crashed:
      return 'crashed'
    case RuntimeState.Deleting:
    case RuntimeState.Deleted:
      return 'gone'
    case RuntimeState.Suspended:
    case RuntimeState.Suspending:
      return 'sleeping'
    case RuntimeState.Booting:
    case RuntimeState.Bootstrapping:
    case RuntimeState.Waking:
    case RuntimeState.Pending:
      return 'warming up'
    case RuntimeState.Online:
      return turnState === 'running' ? 'running…' : 'idle'
    default:
      return null
  }
}
