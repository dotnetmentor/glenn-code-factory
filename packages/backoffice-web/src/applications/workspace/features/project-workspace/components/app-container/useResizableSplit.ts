import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'

interface UseResizableSplitOptions {
  /**
   * localStorage key used to persist the user's preferred split fraction.
   * Callers typically scope this per project + branch so the ratio survives
   * reloads but doesn't bleed across unrelated surfaces.
   */
  storageKey: string
  /** Initial fraction the left pane occupies when no stored value exists. */
  defaultFraction: number
  /** Minimum fraction the left pane is allowed to shrink to. */
  minFraction?: number
  /** Maximum fraction the left pane is allowed to grow to. */
  maxFraction?: number
  /** Ref to the row element used to measure container width during drag. */
  containerRef: RefObject<HTMLElement | null>
}

interface UseResizableSplitResult {
  /** Current left-pane fraction in [minFraction, maxFraction]. */
  chatFraction: number
  /** Attach to the drag handle's onMouseDown to start a resize gesture. */
  onResizeStart: (event: React.MouseEvent) => void
  /** True while the user is actively dragging the handle. */
  isResizing: boolean
}

/**
 * Hand-rolled horizontal split-pane hook (no library deps).
 *
 * <p>The Glenn-style workspace pairs the chat surface with the
 * AppContainer in a single row. Users on a 14" laptop want a different
 * ratio than users on a 27" display, and that preference should survive a
 * page reload — so we persist the fraction to {@code localStorage} keyed by
 * the caller (typically per project + branch).</p>
 *
 * <p>Gesture: on {@code mousedown} we capture {@code pageX} and the current
 * container width, then attach {@code mousemove} + {@code mouseup} to the
 * window. Each move recomputes the fraction from cursor-x relative to the
 * container's bounding rect, clamped to {@code [minFraction, maxFraction]}.
 * During the gesture we tint the body cursor and disable text selection so
 * the drag feels purposeful and never accidentally selects chat text.</p>
 */
export function useResizableSplit({
  storageKey,
  defaultFraction,
  minFraction = 0.25,
  maxFraction = 0.8,
  containerRef,
}: UseResizableSplitOptions): UseResizableSplitResult {
  const [chatFraction, setChatFraction] = useState<number>(() => {
    if (typeof window === 'undefined') return defaultFraction
    try {
      const raw = window.localStorage.getItem(storageKey)
      if (!raw) return defaultFraction
      const parsed = Number.parseFloat(raw)
      if (!Number.isFinite(parsed)) return defaultFraction
      return clamp(parsed, minFraction, maxFraction)
    } catch {
      return defaultFraction
    }
  })

  const [isResizing, setIsResizing] = useState(false)
  const isResizingRef = useRef(false)

  // Re-hydrate when the storage key changes (e.g. user navigates to a
  // different project/branch). Without this the previous project's ratio
  // would briefly persist into the new one.
  useEffect(() => {
    if (typeof window === 'undefined') return
    try {
      const raw = window.localStorage.getItem(storageKey)
      if (!raw) {
        setChatFraction(defaultFraction)
        return
      }
      const parsed = Number.parseFloat(raw)
      if (!Number.isFinite(parsed)) {
        setChatFraction(defaultFraction)
        return
      }
      setChatFraction(clamp(parsed, minFraction, maxFraction))
    } catch {
      setChatFraction(defaultFraction)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey])

  // Persist on every settled value. Throttling isn't worth it — setState
  // already coalesces, and the writes are tiny.
  useEffect(() => {
    if (typeof window === 'undefined') return
    try {
      window.localStorage.setItem(storageKey, chatFraction.toFixed(4))
    } catch {
      // Quota / private-mode failures are non-fatal — the ratio just
      // resets on the next reload.
    }
  }, [storageKey, chatFraction])

  const onResizeStart = useCallback(
    (event: React.MouseEvent) => {
      // Only honor primary-button drags. Right-click on the handle should
      // do nothing.
      if (event.button !== 0) return
      event.preventDefault()

      isResizingRef.current = true
      setIsResizing(true)

      const originalBodyCursor = document.body.style.cursor
      const originalBodyUserSelect = document.body.style.userSelect
      document.body.style.cursor = 'col-resize'
      document.body.style.userSelect = 'none'

      const handleMove = (e: MouseEvent) => {
        if (!isResizingRef.current) return
        const container = containerRef.current
        if (!container) return
        const rect = container.getBoundingClientRect()
        if (rect.width <= 0) return
        const next = (e.clientX - rect.left) / rect.width
        setChatFraction(clamp(next, minFraction, maxFraction))
      }

      const handleUp = () => {
        isResizingRef.current = false
        setIsResizing(false)
        document.body.style.cursor = originalBodyCursor
        document.body.style.userSelect = originalBodyUserSelect
        window.removeEventListener('mousemove', handleMove)
        window.removeEventListener('mouseup', handleUp)
      }

      window.addEventListener('mousemove', handleMove)
      window.addEventListener('mouseup', handleUp)
    },
    [containerRef, minFraction, maxFraction],
  )

  // Defence in depth: if this hook unmounts mid-drag (rare — only if the
  // whole route unmounts), restore the body styles so the user isn't left
  // with a col-resize cursor.
  useEffect(() => {
    return () => {
      if (isResizingRef.current) {
        document.body.style.cursor = ''
        document.body.style.userSelect = ''
      }
    }
  }, [])

  return { chatFraction, onResizeStart, isResizing }
}

function clamp(value: number, min: number, max: number): number {
  if (value < min) return min
  if (value > max) return max
  return value
}
