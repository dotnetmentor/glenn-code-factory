/**
 * useStickToBottom — industry-standard "sticky-when-at-bottom" scroll behavior.
 *
 * <p>Replaces our previous bespoke stepped-scroll machinery. This is the
 * pattern used by ChatGPT, Claude.ai, Perplexity, Slack, Discord, and Vercel
 * AI Elements: while the viewport is within {@code bottomThreshold} pixels of
 * the bottom we auto-snap to the new bottom on every content growth; the
 * moment the user scrolls up we release the stick and reveal a "jump to
 * latest" affordance.</p>
 *
 * <h2>Why we listen to wheel/touch/keydown instead of the scroll event</h2>
 * <p>The {@code scroll} event fires for BOTH user-initiated scrolls AND
 * programmatic scrolls (including the ones our own ResizeObserver auto-snap
 * fires). Driving sticky state off {@code scroll} causes the state to flicker
 * around the threshold whenever auto-snap runs — you can see the "Jump to
 * latest" pill blip into existence for a single frame mid-stream.</p>
 * <p>By listening to the input intents that ONLY a human can produce
 * ({@code wheel}, {@code touchmove}, navigation {@code keydown}s) and
 * deferring the measurement until after the browser has applied the resulting
 * scroll, we cleanly separate "user moved the viewport" from "we moved the
 * viewport," and the sticky state stays stable.</p>
 *
 * <h2>Why a hybrid ref-callback + RefObject return type</h2>
 * <p>The scroll container is conditionally rendered in ChatCanvas — the
 * empty-state branch doesn't include the scroll {@code Box} at all, so on a
 * fresh conversation the hook mounts with no element attached yet. If we used
 * plain {@code useRef}, the {@code useEffect}s would all bail out on
 * {@code if (!el) return} during their first (and only) run, and React would
 * never re-run them when the ref later filled in — mutating a {@code useRef}'s
 * {@code .current} doesn't trigger effects.</p>
 * <p>The fix is to expose ref CALLBACKS that mirror the attached element into
 * {@code useState}, and key the effects on that state. When the JSX with the
 * ref finally mounts on a later render, React calls our callback, state
 * updates, and the effects re-run — wiring up the ResizeObserver and the
 * wheel/touch/keydown listeners exactly when the element appears.</p>
 * <p>To keep ChatCanvas's direct {@code scrollRef.current} reads working
 * (used by the on-send {@code scrollIntoView} dance and the "show all turns"
 * scroll-position preservation), the callback is also a real
 * {@code RefObject}: it has a {@code .current} field backed by an internal
 * {@code useRef} that the callback writes to alongside the {@code setState}.
 * Consumers can keep using {@code ref={scrollRef}} in JSX and
 * {@code scrollRef.current} in callbacks without noticing the change.</p>
 *
 * <h2>Credit</h2>
 * <p>Hand-port of the algorithm from Vercel AI Elements'
 * {@code use-stick-to-bottom} hook (MIT) —
 * {@link https://github.com/vercel/ai/tree/main/packages/elements}. We do NOT
 * take a dependency on the upstream package; the algorithm is small enough
 * that a local port is preferable so we can tune it for our exact UI without
 * version churn. Compare against upstream when chasing edge cases.</p>
 */
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { RefCallback, RefObject } from 'react'

export interface UseStickToBottomOptions {
  /** Pixels of slack at the bottom that still count as "at bottom".
   *  Default 24 — about one line of body text. */
  bottomThreshold?: number
  /** Opaque value that, whenever it changes, forces a re-snap to the bottom
   *  and re-arms the sticky state. Pass the active conversation id so that
   *  switching conversations lands the viewport on the newest content (like
   *  any chat app) rather than staying parked where the previous conversation
   *  left it. The scroll {@code Box} stays mounted across switches, so the
   *  scroll element doesn't change and the initial-arrival snap won't re-fire
   *  on its own — this key gives it a reason to. */
  resetKey?: string | number
}

/**
 * Hybrid ref: a callback (so React's {@code ref={...}} prop populates it)
 * that also has a readable {@code .current} field (so existing call sites
 * doing {@code scrollRef.current} keep working). See the
 * {@code UseStickToBottomReturn} javadoc above for the motivation.
 */
export type HybridRef<T> = RefCallback<T> & RefObject<T | null>

export interface UseStickToBottomReturn<T extends HTMLElement> {
  scrollRef: HybridRef<T>
  /** Wrap the scrollable CONTENT (the children that grow) in a div with
   *  this ref so we can observe its height changes (markdown reflow,
   *  late-mounting code blocks, image loads, etc.). */
  contentRef: HybridRef<HTMLDivElement>
  /** Re-renders when the viewport crosses the at-bottom threshold —
   *  drives "Jump to latest" visibility. */
  isAtBottom: boolean
  /** Programmatic scroll to scrollHeight. Re-arms the sticky state on
   *  completion. Default behavior is 'auto' (instant); pass 'smooth' for
   *  the user-clicked "Jump to latest" affordance. */
  scrollToBottom: (behavior?: ScrollBehavior) => void
}

const DEFAULT_BOTTOM_THRESHOLD = 24
/** Browsers don't fire a "smooth scroll complete" event; 350ms comfortably
 *  covers a typical same-document smooth scroll, so we defer re-arming the
 *  sticky state until then. */
const SMOOTH_SCROLL_REARM_MS = 350
/** Keys that can move the viewport. Letter keys are EXCLUDED — if the user
 *  is typing into a contenteditable inside the scroll container we don't
 *  want to redo measurements on every keystroke. */
const SCROLL_KEYS = new Set([
  'PageUp',
  'PageDown',
  'ArrowUp',
  'ArrowDown',
  'Home',
  'End',
  ' ', // Space — pages down
])

/**
 * Pure measurement: is the viewport within {@code threshold} pixels of the
 * bottom of the scroll content? Defensive against {@code null} (returns
 * {@code true} so the initial state is "stuck" rather than "released").
 */
function measureIsAtBottom(
  el: HTMLElement | null,
  threshold: number,
): boolean {
  if (!el) return true
  return el.scrollHeight - (el.scrollTop + el.clientHeight) <= threshold
}

export function useStickToBottom<T extends HTMLElement = HTMLDivElement>(
  options?: UseStickToBottomOptions,
): UseStickToBottomReturn<T> {
  const bottomThreshold = options?.bottomThreshold ?? DEFAULT_BOTTOM_THRESHOLD
  const resetKey = options?.resetKey

  // ── Hybrid refs ──────────────────────────────────────────────────────────
  // Two mirrors per ref: a useState for the effects (so they re-run when the
  // element appears or changes) and a useRef for cheap synchronous .current
  // reads from event handlers / callbacks. The exposed `scrollRef` /
  // `contentRef` are callable (React calls them when the ref attaches) AND
  // expose a `.current` accessor — see HybridRef.
  const [scrollEl, setScrollEl] = useState<T | null>(null)
  const [contentEl, setContentEl] = useState<HTMLDivElement | null>(null)
  const scrollCurrentRef = useRef<T | null>(null)
  const contentCurrentRef = useRef<HTMLDivElement | null>(null)

  const scrollRef = useMemo<HybridRef<T>>(() => {
    const cb = ((el: T | null) => {
      scrollCurrentRef.current = el
      setScrollEl(el)
    }) as HybridRef<T>
    // Make `cb.current` track the underlying useRef so reads stay live.
    Object.defineProperty(cb, 'current', {
      configurable: true,
      get: () => scrollCurrentRef.current,
      set: (v: T | null) => {
        // Some legacy callers (and our own tests historically) assigned
        // directly to `.current`. Mirror to state so effects still wake.
        scrollCurrentRef.current = v
        setScrollEl(v)
      },
    })
    return cb
  }, [])

  const contentRef = useMemo<HybridRef<HTMLDivElement>>(() => {
    const cb = ((el: HTMLDivElement | null) => {
      contentCurrentRef.current = el
      setContentEl(el)
    }) as HybridRef<HTMLDivElement>
    Object.defineProperty(cb, 'current', {
      configurable: true,
      get: () => contentCurrentRef.current,
      set: (v: HTMLDivElement | null) => {
        contentCurrentRef.current = v
        setContentEl(v)
      },
    })
    return cb
  }, [])

  // Two mirrors of the same boolean: state for re-renders (the "Jump to
  // latest" pill flips on it), ref for effects/handlers that need the
  // current value without stale closures.
  const [isAtBottom, setIsAtBottom] = useState(true)
  const isAtBottomRef = useRef(true)
  const setSticky = useCallback((next: boolean) => {
    if (isAtBottomRef.current === next) return
    isAtBottomRef.current = next
    setIsAtBottom(next)
  }, [])

  // The scrollTop value the hook itself most recently wrote. Used by the
  // ResizeObserver auto-snap to distinguish "the browser is still where we
  // last put it" (snap on growth) from "some other code moved the viewport"
  // (release stickiness, don't fight). null on first mount before any write.
  const lastSetScrollTopRef = useRef<number | null>(null)

  // Post-reset "pin to bottom" settle window. A conversation switch wipes the
  // transcript and the active session re-hydrates asynchronously over several
  // frames (state-clear render → tail fetch → render → markdown/code reflow).
  // A single one-shot snap fires against the STALE/CLEARED content (wrong
  // scrollHeight) and is effectively a no-op; the ResizeObserver rescue is
  // timing-dependent and, worse, the wipe forces scrollTop to 0 which trips
  // its external-scroll check and RELEASES stickiness — stranding the user at
  // an earlier message. To make the landing robust we keep the viewport pinned
  // to the bottom for a short window after the reset: every animation frame we
  // re-snap and re-arm, and the ResizeObserver treats all growth as "snap"
  // (skipping its external-scroll release) while the window is open. The window
  // is a timestamp (performance.now() deadline); 0 means "not pinning". A
  // genuine user scroll (wheel/touch/keydown) cancels it immediately so we
  // never fight a user who scrolls up mid-hydration.
  const pinUntilRef = useRef(0)
  const pinRafRef = useRef<number | null>(null)
  // How long to keep re-snapping after a reset. Comfortably covers the
  // observed ~870ms stale-content window plus the active session's tail
  // fetch + render + reflow, without being long enough to feel "stuck" if the
  // user immediately tries to scroll (their first wheel/touch/keydown cancels
  // it regardless).
  const PIN_TO_BOTTOM_WINDOW_MS = 1500

  // Write scrollTop and remember the clamped post-write value. In real
  // browsers `el.scrollTop = X` is clamped to `scrollHeight - clientHeight`,
  // so we read the property back rather than trusting the value we assigned —
  // otherwise the auto-snap's external-scroll check would incorrectly flag
  // every snap as "someone moved us."
  const writeScrollTop = useCallback((el: HTMLElement, top: number) => {
    el.scrollTop = top
    lastSetScrollTopRef.current = el.scrollTop
  }, [])

  const writeScrollTo = useCallback(
    (el: HTMLElement, top: number, behavior: ScrollBehavior) => {
      if (behavior === 'auto') {
        writeScrollTop(el, top)
        return
      }
      // Smooth-scroll: the animation runs on the browser's clock, so the
      // final clamped value isn't readable yet. Record the TARGET — the
      // mid-animation scrollTop values will differ, but content growth
      // during a smooth scroll is rare (the user just clicked a button —
      // they're not streaming yet) and the re-arm timer will reset state.
      el.scrollTo({ top, behavior })
      lastSetScrollTopRef.current = top
    },
    [writeScrollTop],
  )

  // Initial-element-arrival snap. Fires when the scroll element first attaches
  // (on a fresh conversation that's after a re-render, not the first commit —
  // see the hook javadoc on the hybrid-ref rationale) and re-fires if the
  // element instance ever changes. Also re-fires whenever {@code resetKey}
  // changes (conversation switch): the scroll Box stays mounted across
  // switches so {@code scrollEl} is stable, and the user may have scrolled up
  // in the previous conversation (so the ResizeObserver auto-snap is
  // suppressed) — keying this on {@code resetKey} guarantees we land at the
  // bottom of the freshly-wiped content and re-arm stickiness so the active
  // session's hydrating tail keeps the viewport pinned. Synchronous-before-
  // paint so the user never sees the transcript momentarily parked at the top.
  useLayoutEffect(() => {
    if (!scrollEl) return
    // Immediate snap (covers the element-arrival case where content is already
    // present), then open the pin window so we keep re-snapping across the
    // active session's async hydration. See pinUntilRef's javadoc.
    writeScrollTop(scrollEl, scrollEl.scrollHeight)
    setSticky(true)

    pinUntilRef.current =
      (typeof performance !== 'undefined' ? performance.now() : Date.now()) +
      PIN_TO_BOTTOM_WINDOW_MS

    const now = () =>
      typeof performance !== 'undefined' ? performance.now() : Date.now()
    const tick = () => {
      // Cancelled by a user scroll (pinUntilRef zeroed) or window elapsed.
      if (pinUntilRef.current === 0 || now() >= pinUntilRef.current) {
        pinUntilRef.current = 0
        pinRafRef.current = null
        return
      }
      // Keep the viewport glued to the bottom as content grows/shrinks during
      // hydration. Forcing sticky here means the ResizeObserver's growth snap
      // stays armed even though the wipe reset scrollTop to 0.
      writeScrollTop(scrollEl, scrollEl.scrollHeight)
      setSticky(true)
      pinRafRef.current = requestAnimationFrame(tick)
    }
    pinRafRef.current = requestAnimationFrame(tick)

    return () => {
      if (pinRafRef.current !== null) {
        cancelAnimationFrame(pinRafRef.current)
        pinRafRef.current = null
      }
      pinUntilRef.current = 0
    }
  }, [scrollEl, resetKey, writeScrollTop, setSticky])

  // Auto-snap on content growth. ResizeObserver fires for markdown reflow,
  // syntax highlighter mounting, image loads — anything that resizes the
  // content without a React render. INSTANT scroll only: smooth-scrolling
  // here is what produces the jitter we're trying to remove.
  useEffect(() => {
    if (!contentEl) return
    const observer = new ResizeObserver(() => {
      if (!scrollEl) return
      // During the post-reset pin window, always snap to the new bottom and
      // skip the external-scroll release check: the conversation wipe forces
      // scrollTop to 0 (content shrank), which would otherwise read as "someone
      // moved us" and release stickiness, stranding the user mid-transcript.
      const now =
        typeof performance !== 'undefined' ? performance.now() : Date.now()
      if (pinUntilRef.current !== 0 && now < pinUntilRef.current) {
        writeScrollTop(scrollEl, scrollEl.scrollHeight)
        setSticky(true)
        return
      }
      if (!isAtBottomRef.current) return
      // External-scroll detection: if the current scrollTop differs from
      // the value WE last set, some other code (the on-send scrollIntoView
      // on the user bubble, a future imperative caller, scroll-restoration,
      // anchor navigation) moved the viewport between our last write and
      // now. Respect that — release stickiness and skip the snap. The
      // browser's scroll-anchoring may also nudge scrollTop during reflow,
      // so we allow a small tolerance.
      //
      // The "first growth after mount" case is handled by `lastSetScrollTopRef`
      // being seeded by the initial-mount useLayoutEffect.
      const last = lastSetScrollTopRef.current
      const EXTERNAL_SCROLL_TOLERANCE_PX = 2
      if (
        last !== null &&
        Math.abs(scrollEl.scrollTop - last) > EXTERNAL_SCROLL_TOLERANCE_PX
      ) {
        setSticky(false)
        return
      }
      writeScrollTop(scrollEl, scrollEl.scrollHeight)
    })
    observer.observe(contentEl)
    return () => observer.disconnect()
  }, [contentEl, scrollEl, setSticky, writeScrollTop])

  // User-scroll detection. We listen to the input intents (wheel, touch,
  // keydown) instead of the {@code scroll} event so programmatic scrolls
  // (our own ResizeObserver auto-snap, the on-send scrollIntoView) don't
  // flicker the sticky state. The microtask defer lets the browser apply
  // the scroll first so we measure the post-scroll position.
  useEffect(() => {
    if (!scrollEl) return

    const reevaluate = () => {
      // A real user-input intent cancels any in-flight post-reset pin window so
      // we never fight a user who scrolls up while the active session is still
      // hydrating. The microtask defer then measures the post-scroll position.
      pinUntilRef.current = 0
      queueMicrotask(() => {
        setSticky(measureIsAtBottom(scrollEl, bottomThreshold))
      })
    }

    const onWheel = () => reevaluate()
    const onTouchMove = () => reevaluate()
    const onKeyDown = (ev: KeyboardEvent) => {
      if (SCROLL_KEYS.has(ev.key)) reevaluate()
    }

    scrollEl.addEventListener('wheel', onWheel, { passive: true })
    scrollEl.addEventListener('touchmove', onTouchMove, { passive: true })
    scrollEl.addEventListener('keydown', onKeyDown)

    return () => {
      scrollEl.removeEventListener('wheel', onWheel)
      scrollEl.removeEventListener('touchmove', onTouchMove)
      scrollEl.removeEventListener('keydown', onKeyDown)
    }
  }, [scrollEl, bottomThreshold, setSticky])

  const scrollToBottom = useCallback(
    (behavior: ScrollBehavior = 'auto') => {
      if (!scrollEl) return
      writeScrollTo(scrollEl, scrollEl.scrollHeight, behavior)
      if (behavior === 'auto') {
        setSticky(true)
        return
      }
      // Smooth: browsers don't fire a "scroll complete" event, so defer the
      // re-arm with a timer that comfortably covers the animation.
      window.setTimeout(() => setSticky(true), SMOOTH_SCROLL_REARM_MS)
    },
    [scrollEl, setSticky, writeScrollTo],
  )

  return { scrollRef, contentRef, isAtBottom, scrollToBottom }
}
