import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

/**
 * The views the bottom debug panel can show. Each one swaps out the
 * panel body while the chrome strip (segmented switcher, close button)
 * stays put — preventing any layout shift between views.
 *
 * <p>{@code 'fly'} and {@code 'spec'} are superadmin-only views; the
 * segmented switcher filters them out for non-superadmins and the saved-view
 * restore below falls back to {@code 'logs'} when the persisted view is one
 * of those but the user has lost the role.</p>
 */
export type RuntimeDebugPanelView =
  | 'logs'
  | 'services'
  | 'timeline'
  | 'sysstats'
  | 'spec'
  | 'fly'

/** localStorage key for the user's last-chosen view. */
const VIEW_STORAGE_KEY = 'workspace.runtimeDebugPanel.view'

const VALID_VIEWS: ReadonlyArray<RuntimeDebugPanelView> = [
  'logs',
  'services',
  'timeline',
  'sysstats',
  'spec',
  'fly',
]

function isValidView(value: string | null): value is RuntimeDebugPanelView {
  return value !== null && (VALID_VIEWS as readonly string[]).includes(value)
}

/**
 * Shape exposed by {@link RuntimeDebugPanelProvider}. Lives at the
 * {@code ProjectWorkspaceShell} level because three siblings have to agree on
 * one piece of state:
 *
 * <ol>
 *   <li>The {@code ConversationSidebar} footer button (which opens the panel).</li>
 *   <li>The {@code ServicesTab} "View logs" callback (also opens the panel
 *       with a service pre-seeded — fired from inside the right-anchored
 *       project settings drawer).</li>
 *   <li>The bottom {@code RuntimeLogsPanel} that actually renders and tails
 *       the SignalR subscription.</li>
 * </ol>
 *
 * <p>Lifting via context (rather than threading callbacks through five
 * components) keeps the call sites small.</p>
 */
export interface RuntimeDebugPanelContextValue {
  /** Whether the bottom log panel is currently visible. */
  open: boolean
  /** Optional service to pre-select on the next open (used by Logs view). */
  initialServiceName: string | undefined
  /** Currently-active view inside the panel. Persisted to localStorage. */
  activeView: RuntimeDebugPanelView
  /** Open the panel, optionally pre-selecting a service. Forces Logs view. */
  openPanel: (serviceName?: string) => void
  /** Close the panel. The component handles its own exit animation. */
  closePanel: () => void
  /** Toggle the panel; clears any pre-selected service when closing. */
  togglePanel: () => void
  /**
   * Switch which view the panel is showing. When switching TO Logs, an
   * optional service name lets the call site pre-select a row (e.g. the
   * "View logs" link inside the Services view). For other views the
   * argument is ignored.
   */
  setActiveView: (view: RuntimeDebugPanelView, serviceName?: string) => void
}

const RuntimeDebugPanelContext = createContext<RuntimeDebugPanelContextValue | null>(null)

export interface RuntimeDebugPanelProviderProps {
  children: ReactNode
}

/**
 * Provider that owns the bottom log panel's open / service-selection / view
 * state. Wrap once around the {@code ProjectWorkspaceShell} (or any subtree
 * that needs both the panel <em>and</em> the affordances that open it).
 */
export function RuntimeDebugPanelProvider({ children }: RuntimeDebugPanelProviderProps) {
  const [open, setOpen] = useState(false)
  const [initialServiceName, setInitialServiceName] = useState<string | undefined>(undefined)

  const [activeView, setActiveViewState] = useState<RuntimeDebugPanelView>(() => {
    if (typeof window === 'undefined') return 'logs'
    const stored = window.localStorage.getItem(VIEW_STORAGE_KEY)
    return isValidView(stored) ? stored : 'logs'
  })

  // Persist view changes — keeps the user's last-chosen surface stable across
  // page reloads (the panel reopens in Timeline if that's where they were).
  useEffect(() => {
    if (typeof window === 'undefined') return
    try {
      window.localStorage.setItem(VIEW_STORAGE_KEY, activeView)
    } catch {
      // localStorage might be unavailable in incognito / quota-locked
      // contexts — best-effort persistence only.
    }
  }, [activeView])

  const openPanel = useCallback((serviceName?: string) => {
    setInitialServiceName(serviceName)
    // Opening with a service name implies the caller wants the Logs view
    // (the "View logs" link from the Services tab). A bare openPanel() call
    // respects the user's last-chosen view from localStorage.
    if (serviceName) {
      setActiveViewState('logs')
    }
    setOpen(true)
  }, [])

  const closePanel = useCallback(() => {
    setOpen(false)
  }, [])

  const togglePanel = useCallback(() => {
    setOpen((prev) => {
      if (prev) {
        // Clear pre-seeded service on close so a subsequent toggle-open lands
        // on the user's last manual selection, not a stale cross-link.
        setInitialServiceName(undefined)
      }
      return !prev
    })
  }, [])

  const setActiveView = useCallback(
    (view: RuntimeDebugPanelView, serviceName?: string) => {
      // Switching to Logs with a service name pre-selects that service in the
      // in-panel picker — used by the "View logs" link inside the Services
      // view so the user can flip to Logs without losing context.
      if (view === 'logs' && serviceName) {
        setInitialServiceName(serviceName)
      }
      setActiveViewState(view)
    },
    [],
  )

  const value = useMemo<RuntimeDebugPanelContextValue>(
    () => ({
      open,
      initialServiceName,
      activeView,
      openPanel,
      closePanel,
      togglePanel,
      setActiveView,
    }),
    [open, initialServiceName, activeView, openPanel, closePanel, togglePanel, setActiveView],
  )

  return (
    <RuntimeDebugPanelContext.Provider value={value}>{children}</RuntimeDebugPanelContext.Provider>
  )
}

/**
 * Hook accessor for {@link RuntimeDebugPanelContextValue}. Returns a no-op
 * fallback when used outside a provider so a stray call from the wrong
 * surface (e.g. an isolated unit test) doesn't crash — the button just won't
 * do anything until a real provider mounts.
 */
export function useRuntimeDebugPanel(): RuntimeDebugPanelContextValue {
  const ctx = useContext(RuntimeDebugPanelContext)
  if (ctx) return ctx
  return {
    open: false,
    initialServiceName: undefined,
    activeView: 'logs',
    openPanel: () => {},
    closePanel: () => {},
    togglePanel: () => {},
    setActiveView: () => {},
  }
}
