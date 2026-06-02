import { useEffect, useRef, useState } from 'react'
import { deriveMovie, FINALE_AT, FINALE_STATE, type MovieState } from './script'

/**
 * Drives the landing movie: a single requestAnimationFrame loop that advances
 * elapsed time and re-derives {@link MovieState}, then stops at the finale so
 * the page rests on the live waitlist (per product decision: play once, settle).
 *
 * Honours `prefers-reduced-motion` — when set, we skip the loop entirely and
 * render the resolved finale frame (preview live, transcript complete).
 */
export function useMovie(): MovieState {
  const prefersReduced =
    typeof window !== 'undefined' &&
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

  const [state, setState] = useState<MovieState>(() =>
    prefersReduced ? FINALE_STATE : deriveMovie(0),
  )
  const rafRef = useRef<number | null>(null)
  const startRef = useRef<number | null>(null)

  useEffect(() => {
    if (prefersReduced) {
      setState(FINALE_STATE)
      return
    }

    const tick = (now: number) => {
      if (startRef.current === null) startRef.current = now
      const elapsed = now - startRef.current
      setState(deriveMovie(Math.min(elapsed, FINALE_AT)))

      if (elapsed < FINALE_AT) {
        rafRef.current = requestAnimationFrame(tick)
      }
    }

    rafRef.current = requestAnimationFrame(tick)
    return () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current)
      startRef.current = null
    }
  }, [prefersReduced])

  return state
}
