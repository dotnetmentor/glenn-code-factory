/**
 * useStickToBottom — unit tests for the sticky-when-at-bottom hook.
 *
 * <p>happy-dom doesn't compute layout, so every test fabricates a real
 * {@code HTMLDivElement} and overrides {@code scrollHeight},
 * {@code clientHeight}, and the {@code scrollTop} setter via
 * {@code Object.defineProperty}. A mock {@code ResizeObserver} (whose
 * instance we capture per-test) lets us drive content-growth callbacks
 * deterministically.</p>
 *
 * <p>Because the hook only registers its observer / listeners inside
 * {@code useEffect}s — which need the refs already attached when they run —
 * we mount the hook through a tiny React component (built via
 * {@code createElement} so this file stays {@code .ts}) that attaches our
 * test elements to the refs in a {@code useLayoutEffect}. That layout effect
 * fires after the first commit but before paint, so by the time the hook's
 * own effects/layout-effects examine the refs they're populated — same
 * timing the real {@code <Box ref={scrollRef}>} usage in ChatCanvas
 * produces.</p>
 *
 * <p>The hook now uses a hybrid ref (a callback that's also a
 * {@code RefObject}); see {@code useStickToBottom.ts} for the design notes.
 * Tests invoke the callback form ({@code r.scrollRef(el)}) to attach, which
 * mirrors how React itself calls ref props at commit time.</p>
 *
 * <p>One {@code it()} per behavior promised by the hook's public API
 * (initial mount, late-arriving refs / fresh-conversation regression,
 * auto-snap on growth, user-scroll detection, scrollToBottom arming,
 * cleanup).</p>
 */
import { createElement, useLayoutEffect } from 'react'
import { act, render, renderHook } from '@testing-library/react'
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest'

import {
  useStickToBottom,
  type UseStickToBottomReturn,
} from '../useStickToBottom'

// ── Mocked DOM element with overridable scroll metrics ─────────────────────

interface MockScrollEl {
  el: HTMLDivElement
  setScrollHeight: (next: number) => void
  getScrollTop: () => number
  setScrollTop: (next: number) => void
}

function makeScrollEl({
  scrollHeight,
  clientHeight,
  initialScrollTop = 0,
}: {
  scrollHeight: number
  clientHeight: number
  initialScrollTop?: number
}): MockScrollEl {
  const el = document.createElement('div')
  let scrollTop = initialScrollTop
  let currentScrollHeight = scrollHeight
  Object.defineProperty(el, 'scrollHeight', {
    configurable: true,
    get: () => currentScrollHeight,
  })
  Object.defineProperty(el, 'clientHeight', {
    configurable: true,
    get: () => clientHeight,
  })
  Object.defineProperty(el, 'scrollTop', {
    configurable: true,
    get: () => scrollTop,
    set: (v: number) => {
      scrollTop = v
    },
  })
  el.scrollTo = ((arg?: ScrollToOptions | number, y?: number) => {
    if (typeof arg === 'number') {
      scrollTop = y ?? 0
    } else {
      scrollTop = arg?.top ?? 0
    }
  }) as HTMLDivElement['scrollTo']
  return {
    el,
    setScrollHeight(next: number) {
      currentScrollHeight = next
    },
    getScrollTop() {
      return scrollTop
    },
    setScrollTop(next: number) {
      scrollTop = next
    },
  }
}

// ── Mock ResizeObserver ────────────────────────────────────────────────────

const observerInstances: MockResizeObserver[] = []

class MockResizeObserver {
  callback: ResizeObserverCallback
  observed: Element[] = []
  disconnected = false
  constructor(cb: ResizeObserverCallback) {
    this.callback = cb
    observerInstances.push(this)
  }
  observe(target: Element) {
    this.observed.push(target)
  }
  unobserve(target: Element) {
    this.observed = this.observed.filter((t) => t !== target)
  }
  disconnect() {
    this.disconnected = true
    this.observed = []
  }
  /** Test-only helper to fire the callback manually. */
  trigger() {
    this.callback([], this as unknown as ResizeObserver)
  }
}

beforeEach(() => {
  observerInstances.length = 0
  ;(globalThis as unknown as { ResizeObserver: typeof MockResizeObserver }).ResizeObserver =
    MockResizeObserver
})

afterEach(() => {
  vi.useRealTimers()
})

// ── Test harness component ────────────────────────────────────────────────

interface HarnessProps {
  scrollEl: HTMLDivElement
  contentEl: HTMLDivElement
  resultRef: { current: UseStickToBottomReturn<HTMLDivElement> | null }
}

/**
 * Tiny React component that runs the hook and attaches the test
 * scroll/content elements to the hook's ref callbacks inside a
 * {@code useLayoutEffect}. That layout effect fires after the first commit
 * but before paint — same timing React itself would use to populate a
 * {@code ref={scrollRef}} prop on a rendered element. The hook's own
 * effects then see the refs populated when they run, exactly as in the
 * real app.
 */
function Harness({ scrollEl, contentEl, resultRef }: HarnessProps) {
  const r = useStickToBottom<HTMLDivElement>()
  resultRef.current = r
  useLayoutEffect(() => {
    r.scrollRef(scrollEl)
    r.contentRef(contentEl)
    return () => {
      r.scrollRef(null)
      r.contentRef(null)
    }
    // We want this to fire on every render so a re-render with a different
    // element re-attaches — but in practice the elements are stable per
    // test, so the body is a no-op after the first run.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scrollEl, contentEl])
  return null
}

interface Mounted {
  scroll: MockScrollEl
  content: HTMLDivElement
  result: { current: UseStickToBottomReturn<HTMLDivElement> | null }
  unmount: () => void
}

function mount({
  scrollHeight,
  clientHeight,
  initialScrollTop = 0,
}: {
  scrollHeight: number
  clientHeight: number
  initialScrollTop?: number
}): Mounted {
  const scroll = makeScrollEl({ scrollHeight, clientHeight, initialScrollTop })
  const content = document.createElement('div')
  const result: { current: UseStickToBottomReturn<HTMLDivElement> | null } = {
    current: null,
  }
  const { unmount } = render(
    createElement(Harness, { scrollEl: scroll.el, contentEl: content, resultRef: result }),
  )
  return { scroll, content, result, unmount }
}

// ── Tests ───────────────────────────────────────────────────────────────────

describe('useStickToBottom — initial state and mount', () => {
  it('starts with isAtBottom === true', () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })
    expect(m.result.current?.isAtBottom).toBe(true)
  })

  it('on mount, snaps scrollTop to scrollHeight when scrollRef + contentRef are attached', () => {
    const m = mount({ scrollHeight: 2000, clientHeight: 400, initialScrollTop: 0 })
    // Layout effect on first mount sets scrollTop = scrollHeight.
    expect(m.scroll.getScrollTop()).toBe(2000)
    expect(m.result.current?.isAtBottom).toBe(true)
  })

  it('attaches listeners and snaps to bottom when refs are attached AFTER initial mount (fresh-convo regression)', async () => {
    // Reproduces the empty-state-to-transcript flow in ChatCanvas: the
    // hook is called unconditionally at the top of the component, but the
    // scroll container is only rendered in the non-empty branch. Refs
    // arrive late. The hook must observe the ref attachment and (a) snap
    // to bottom AND (b) attach wheel/touch/keydown listeners when they
    // first arrive — NOT only on initial mount.
    const { result } = renderHook(() => useStickToBottom<HTMLDivElement>())

    // No refs attached yet — isAtBottom defaults to true, but listeners
    // aren't wired and no snap has run.
    expect(result.current.isAtBottom).toBe(true)

    // Now simulate JSX appearing on a later render and React calling the
    // ref callbacks with the elements (this is what `<Box ref={scrollRef}>`
    // triggers).
    const scroll = makeScrollEl({ scrollHeight: 1000, clientHeight: 600 })
    const content = document.createElement('div')
    act(() => {
      result.current.scrollRef(scroll.el)
      result.current.contentRef(content)
    })

    // Initial-element-arrival snap should have run now.
    expect(scroll.getScrollTop()).toBe(1000)

    // Wheel-up should now flip isAtBottom — proving the listener was
    // attached after the late ref arrival.
    scroll.setScrollTop(100)
    await act(async () => {
      scroll.el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
      // Microtask flush.
      await Promise.resolve()
    })
    expect(result.current.isAtBottom).toBe(false)
  })
})

describe('useStickToBottom — auto-snap on content growth', () => {
  it('content growth while at bottom snaps scrollTop to new scrollHeight', () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    m.scroll.setScrollHeight(1500)
    act(() => {
      observerInstances[observerInstances.length - 1].trigger()
    })

    expect(m.scroll.getScrollTop()).toBe(1500)
  })

  it('content growth after a programmatic scroll-away releases stickiness instead of snapping back', () => {
    // Reproduces the on-send scrollIntoView({block:'start'}) scenario:
    // the hook's wheel/touch/keydown listeners don't fire on programmatic
    // scrolls, so the only signal that "someone else moved the viewport"
    // is comparing actual scrollTop against the value we last wrote. After
    // a programmatic scroll-away the next content growth should RELEASE
    // stickiness, NOT snap back to bottom — otherwise the on-send
    // scroll-to-top behavior in ChatCanvas gets undone on the first
    // streamed chunk.
    //
    // IMPORTANT: this must happen OUTSIDE the post-reset pin window. For
    // PIN_TO_BOTTOM_WINDOW_MS (1500) after mount/conversation-switch the
    // ResizeObserver deliberately force-snaps and SKIPS the external-scroll
    // release check (so async hydration after a conversation wipe doesn't
    // strand the user mid-transcript). The real on-send scroll happens
    // mid-conversation, well after that window has closed — so we drive a
    // fake clock and advance past the window before the growth fires.
    let nowMs = 0
    const nowSpy = vi.spyOn(performance, 'now').mockImplementation(() => nowMs)

    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 0 })

    // After mount snap, hook recorded lastSetScrollTopRef === 1000.
    expect(m.scroll.getScrollTop()).toBe(1000)
    expect(m.result.current?.isAtBottom).toBe(true)

    // Advance past the pin window so the growth below takes the normal
    // external-scroll release path rather than the force-snap pin path.
    nowMs = 1600

    // Simulate the on-send programmatic scroll: move scrollTop directly to
    // a value well away from bottom. NO wheel/touch/keydown event dispatched —
    // this is the deliberately-blind case the bug exercises.
    m.scroll.setScrollTop(100)

    // Fire the ResizeObserver callback (simulating new content arriving).
    m.scroll.setScrollHeight(1200)
    act(() => {
      observerInstances[observerInstances.length - 1].trigger()
    })

    // The snap must NOT have run: scrollTop stays at 100. And the hook
    // must have released stickiness so the Jump-to-latest Fab will appear.
    expect(m.scroll.getScrollTop()).toBe(100)
    expect(m.result.current?.isAtBottom).toBe(false)

    nowSpy.mockRestore()
  })

  it('content growth while NOT at bottom leaves scrollTop alone', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    // User scrolls up — simulate by lowering scrollTop and dispatching wheel.
    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
      // queueMicrotask flush.
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)

    // Grow content — observer should NOT touch scrollTop.
    m.scroll.setScrollHeight(1500)
    act(() => {
      observerInstances[observerInstances.length - 1].trigger()
    })
    expect(m.scroll.getScrollTop()).toBe(0)
  })
})

describe('useStickToBottom — user-scroll detection', () => {
  it('wheel event that lands user away from bottom flips isAtBottom to false', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })
    expect(m.result.current?.isAtBottom).toBe(true)

    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)
  })

  it('touchmove event re-evaluates isAtBottom from scroll position', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new Event('touchmove'))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)

    // Scroll back to bottom and dispatch touchmove — re-pin.
    m.scroll.setScrollTop(600)
    await act(async () => {
      m.scroll.el.dispatchEvent(new Event('touchmove'))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(true)
  })

  it('PageUp keydown re-evaluates isAtBottom', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new KeyboardEvent('keydown', { key: 'PageUp' }))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)
  })

  it('does NOT listen to the scroll event', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new Event('scroll'))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(true)
  })
})

describe('useStickToBottom — scrollToBottom', () => {
  it('scrollToBottom() jumps scrollTop to scrollHeight and re-pins isAtBottom', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    // Unstick first.
    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)

    act(() => {
      m.result.current?.scrollToBottom()
    })
    expect(m.scroll.getScrollTop()).toBe(1000)
    expect(m.result.current?.isAtBottom).toBe(true)
  })

  it('scrollToBottom("smooth") still re-pins after a delay (use fake timers)', async () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400, initialScrollTop: 600 })

    // Unstick (real timers — the wheel handler uses queueMicrotask, not setTimeout).
    m.scroll.setScrollTop(0)
    await act(async () => {
      m.scroll.el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
      await Promise.resolve()
    })
    expect(m.result.current?.isAtBottom).toBe(false)

    // Now switch to fake timers for the deferred re-arm assertion.
    vi.useFakeTimers()
    act(() => {
      m.result.current?.scrollToBottom('smooth')
    })
    expect(m.result.current?.isAtBottom).toBe(false)
    act(() => {
      vi.advanceTimersByTime(400)
    })
    expect(m.result.current?.isAtBottom).toBe(true)
  })
})

describe('useStickToBottom — cleanup', () => {
  it('disconnects the ResizeObserver on unmount', () => {
    const m = mount({ scrollHeight: 1000, clientHeight: 400 })
    const observer = observerInstances[observerInstances.length - 1]
    expect(observer.disconnected).toBe(false)
    m.unmount()
    expect(observer.disconnected).toBe(true)
  })

  it('removes wheel/touch/keydown listeners on unmount', () => {
    const scroll = makeScrollEl({ scrollHeight: 1000, clientHeight: 400 })
    const content = document.createElement('div')
    const removeSpy = vi.spyOn(scroll.el, 'removeEventListener')
    const result: { current: UseStickToBottomReturn<HTMLDivElement> | null } = {
      current: null,
    }
    const { unmount } = render(
      createElement(Harness, { scrollEl: scroll.el, contentEl: content, resultRef: result }),
    )

    unmount()
    const removedEvents = removeSpy.mock.calls.map((c) => c[0])
    expect(removedEvents).toContain('wheel')
    expect(removedEvents).toContain('touchmove')
    expect(removedEvents).toContain('keydown')
  })
})
