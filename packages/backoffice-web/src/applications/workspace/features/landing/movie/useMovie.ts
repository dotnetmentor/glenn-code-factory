import { useCallback, useEffect, useRef, useState } from 'react'
import { deriveMovie, FINALE_AT, FINALE_STATE, type MovieState } from './script'

export interface MovieController {
  state: MovieState
  /** Restart the movie from the top (used by the finale "Run demo again"). */
  replay: () => void
}

/**
 * Drives the landing movie: a single requestAnimationFrame loop that advances
 * elapsed time and re-derives {@link MovieState}, then stops at the finale so
 * the page rests on the live waitlist (per product decision: play once, settle).
 *
 * Honours `prefers-reduced-motion` on first load — we skip the auto-play and
 * render the resolved finale frame. An explicit {@link MovieController.replay}
 * (a user gesture) always animates, reduced-motion or not.
 */
export function useMovie(): MovieController {
  const prefersReduced =
    typeof window !== 'undefined' &&
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

  const [state, setState] = useState<MovieState>(() =>
    prefersReduced ? FINALE_STATE : deriveMovie(0),
  )
  const rafRef = useRef<number | null>(null)
  const startRef = useRef<number | null>(null)

  const run = useCallback(() => {
    if (rafRef.current !== null) cancelAnimationFrame(rafRef.current)
    startRef.current = null

    const tick = (now: number) => {
      if (startRef.current === null) startRef.current = now
      const elapsed = now - startRef.current
      setState(deriveMovie(Math.min(elapsed, FINALE_AT)))

      if (elapsed < FINALE_AT) {
        rafRef.current = requestAnimationFrame(tick)
      }
    }

    rafRef.current = requestAnimationFrame(tick)
  }, [])

  useEffect(() => {
    if (prefersReduced) {
      setState(FINALE_STATE)
      return
    }
    run()
    return () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current)
      startRef.current = null
    }
  }, [prefersReduced, run])

  const replay = useCallback(() => run(), [run])

  return { state, replay }
}
