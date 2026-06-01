/**
 * ActivityPill — state-machine smoke tests (card 6 of cursor-native-chat-ux
 * §3).
 *
 * <p>One {@code it()} per row of the spec §3 table. Each test asserts:
 * <ol>
 *   <li>The label text matches.</li>
 *   <li>The shimmer presence/absence matches the spec.</li>
 *   <li>The status chip variant matches.</li>
 *   <li>The cancel button is present iff the state is live.</li>
 * </ol></p>
 *
 * <p>Plus a fake-timer test for the elapsed counter behaviour and a
 * crossfade test that asserts the label hides during the fade window.</p>
 */
import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { renderHook } from '@testing-library/react'

import { ActivityPill, formatElapsed, useElapsedMs } from '../ActivityPill'
import type { FormatterOutput } from '../toolFormatters'

const FORMATTED: FormatterOutput = {
  activeLabel: 'Running `npm run build`',
  summary: 'Ran `npm run build` — exit 0 (1.4s)',
  errorVariant: 'Ran `npm run build` — exit 1',
  glyph: 'Terminal',
}

function shimmerActive(): boolean {
  const node = screen.getByTestId('pill-shimmer')
  return node.getAttribute('data-shimmer-active') === 'true'
}

function chipVariant(): string | null {
  const node = screen.queryByTestId('pill-status-chip')
  return node?.getAttribute('data-chip-variant') ?? null
}

describe('ActivityPill — state machine (spec §3)', () => {
  it('creating: "Starting…", shimmer on, no chip, cancel available', () => {
    render(<ActivityPill state={{ kind: 'creating' }} onCancel={() => {}} />)
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Starting…')
    expect(shimmerActive()).toBe(true)
    expect(chipVariant()).toBeNull()
    expect(screen.getByTestId('pill-cancel')).toBeInTheDocument()
  })

  it('running-idle (no task): "Working…", shimmer on, no chip', () => {
    render(
      <ActivityPill state={{ kind: 'running-idle' }} onCancel={() => {}} />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Working…')
    expect(shimmerActive()).toBe(true)
    expect(chipVariant()).toBeNull()
    expect(screen.getByTestId('pill-cancel')).toBeInTheDocument()
  })

  it('running-idle (with task): renders task title', () => {
    render(
      <ActivityPill
        state={{ kind: 'running-idle', taskTitle: 'Refactor auth' }}
        onCancel={() => {}}
      />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Refactor auth')
    expect(shimmerActive()).toBe(true)
  })

  it('tool-running: formatter active label + elapsed counter + shimmer', () => {
    render(
      <ActivityPill
        state={{ kind: 'tool-running', formatted: FORMATTED, elapsedMs: 1234 }}
        onCancel={() => {}}
      />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent(
      'Running `npm run build`',
    )
    expect(screen.getByTestId('pill-elapsed')).toHaveTextContent('1.2s')
    expect(shimmerActive()).toBe(true)
    expect(chipVariant()).toBeNull()
    expect(screen.getByTestId('pill-cancel')).toBeInTheDocument()
  })

  it('thinking: "Thinking" label + elapsed + shimmer', () => {
    render(
      <ActivityPill
        state={{ kind: 'thinking', elapsedMs: 3400 }}
        onCancel={() => {}}
      />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Thinking')
    expect(screen.getByTestId('pill-elapsed')).toHaveTextContent('3.4s')
    expect(shimmerActive()).toBe(true)
  })

  it('thinking-done: "Thought" label + duration + green chip + NO shimmer', () => {
    render(
      <ActivityPill
        state={{ kind: 'thinking-done', elapsedMs: 4200 }}
        onCancel={() => {}}
      />,
    )
    // Past-tense — communicates "this thought is done", no nervous shimmer.
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Thought')
    expect(screen.getByTestId('pill-elapsed')).toHaveTextContent('4.2s')
    expect(chipVariant()).toBe('success')
    expect(shimmerActive()).toBe(false)
    // No cancel — the thought is already done.
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })

  it('thinking-done with elapsedMs 0: no elapsed slot rendered', () => {
    render(
      <ActivityPill state={{ kind: 'thinking-done', elapsedMs: 0 }} />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Thought')
    expect(screen.queryByTestId('pill-elapsed')).not.toBeInTheDocument()
    expect(shimmerActive()).toBe(false)
  })

  it('tool-completed: formatter summary + green chip + no shimmer', () => {
    render(
      <ActivityPill state={{ kind: 'tool-completed', formatted: FORMATTED }} />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent(
      'Ran `npm run build` — exit 0 (1.4s)',
    )
    expect(chipVariant()).toBe('success')
    expect(shimmerActive()).toBe(false)
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })

  it('tool-error: formatter errorVariant + amber chip + no shimmer', () => {
    render(
      <ActivityPill state={{ kind: 'tool-error', formatted: FORMATTED }} />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent(
      'Ran `npm run build` — exit 1',
    )
    expect(chipVariant()).toBe('warning')
    expect(shimmerActive()).toBe(false)
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })

  it('cancelling: "Cancelling…" + shimmer + cancel still available', () => {
    render(
      <ActivityPill state={{ kind: 'cancelling' }} onCancel={() => {}} />,
    )
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Cancelling…')
    expect(shimmerActive()).toBe(true)
    expect(screen.getByTestId('pill-cancel')).toBeInTheDocument()
  })

  it('cancelled: "Cancelled" + neutral chip + no shimmer', () => {
    render(<ActivityPill state={{ kind: 'cancelled' }} />)
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Cancelled')
    expect(chipVariant()).toBe('neutral')
    expect(shimmerActive()).toBe(false)
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })

  it('run-error: "Stopped" + red chip + no shimmer', () => {
    render(<ActivityPill state={{ kind: 'run-error' }} />)
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Stopped')
    expect(chipVariant()).toBe('error')
    expect(shimmerActive()).toBe(false)
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })

  it('expired: "Timed out" + neutral chip + no shimmer', () => {
    render(<ActivityPill state={{ kind: 'expired' }} />)
    expect(screen.getByTestId('pill-label')).toHaveTextContent('Timed out')
    expect(chipVariant()).toBe('neutral')
    expect(shimmerActive()).toBe(false)
  })
})

describe('ActivityPill — cancel behaviour', () => {
  it('fires onCancel when cancel button is clicked', () => {
    const onCancel = vi.fn()
    render(
      <ActivityPill
        state={{ kind: 'tool-running', formatted: FORMATTED, elapsedMs: 0 }}
        onCancel={onCancel}
      />,
    )
    screen.getByTestId('pill-cancel').click()
    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('omits cancel button when onCancel is not provided', () => {
    render(
      <ActivityPill
        state={{ kind: 'tool-running', formatted: FORMATTED, elapsedMs: 0 }}
      />,
    )
    expect(screen.queryByTestId('pill-cancel')).not.toBeInTheDocument()
  })
})

describe('ActivityPill — testIdPrefix', () => {
  it('prefixes every emitted testid when provided', () => {
    render(
      <ActivityPill
        state={{ kind: 'tool-completed', formatted: FORMATTED }}
        testIdPrefix="turn-7"
      />,
    )
    expect(screen.getByTestId('turn-7-pill-root')).toBeInTheDocument()
    expect(screen.getByTestId('turn-7-pill-label')).toBeInTheDocument()
    expect(screen.getByTestId('turn-7-pill-status-chip')).toBeInTheDocument()
  })
})

describe('ActivityPill — crossfade', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('hides label during the fade window after a state change', () => {
    const { rerender } = render(
      <ActivityPill state={{ kind: 'creating' }} onCancel={() => {}} />,
    )

    // First render — no fade in progress.
    expect(screen.getByTestId('pill-label').getAttribute('data-fading')).toBe(
      'false',
    )

    // State change triggers the useEffect → setFading(true).
    rerender(
      <ActivityPill state={{ kind: 'running-idle' }} onCancel={() => {}} />,
    )
    expect(screen.getByTestId('pill-label').getAttribute('data-fading')).toBe(
      'true',
    )

    // After the fade window, the flag flips back.
    act(() => {
      vi.advanceTimersByTime(200) // > LABEL_FADE_MS (150)
    })
    expect(screen.getByTestId('pill-label').getAttribute('data-fading')).toBe(
      'false',
    )
  })

  it('does NOT crossfade on elapsed-only updates inside the same kind', () => {
    const { rerender } = render(
      <ActivityPill
        state={{ kind: 'tool-running', formatted: FORMATTED, elapsedMs: 100 }}
        onCancel={() => {}}
      />,
    )
    expect(screen.getByTestId('pill-label').getAttribute('data-fading')).toBe(
      'false',
    )

    // Tick the elapsed counter — no state-kind change.
    rerender(
      <ActivityPill
        state={{ kind: 'tool-running', formatted: FORMATTED, elapsedMs: 200 }}
        onCancel={() => {}}
      />,
    )

    // Label stays stable (no fade triggered) — counter updates underneath.
    expect(screen.getByTestId('pill-label').getAttribute('data-fading')).toBe(
      'false',
    )
    expect(screen.getByTestId('pill-elapsed')).toHaveTextContent('0.2s')
  })
})

describe('formatElapsed', () => {
  it('renders under 10s as tenths', () => {
    expect(formatElapsed(400)).toBe('0.4s')
    expect(formatElapsed(0)).toBe('0.0s')
    expect(formatElapsed(1234)).toBe('1.2s')
    expect(formatElapsed(9999)).toBe('10.0s')
  })
  it('renders 10s–59s as whole seconds', () => {
    expect(formatElapsed(10_000)).toBe('10s')
    expect(formatElapsed(12_345)).toBe('12s')
    expect(formatElapsed(59_999)).toBe('59s')
  })
  it('renders minutes+seconds with zero-padded seconds', () => {
    expect(formatElapsed(60_000)).toBe('1m 00s')
    expect(formatElapsed(74_000)).toBe('1m 14s')
  })
  it('renders hours+minutes for hour-scale durations', () => {
    expect(formatElapsed(3_600_000)).toBe('1h 00m')
    expect(formatElapsed(3_720_000)).toBe('1h 02m')
  })
  it('handles invalid input gracefully', () => {
    expect(formatElapsed(-1)).toBe('0.0s')
    expect(formatElapsed(Number.NaN)).toBe('0.0s')
  })
})

describe('useElapsedMs', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('ticks at ~10fps while active', () => {
    const start = Date.now()
    const { result } = renderHook(() => useElapsedMs(start, true))

    // Initial render — within a few ms of start.
    expect(result.current).toBeLessThan(20)

    act(() => {
      vi.advanceTimersByTime(500)
    })
    // After 500ms we should have ticked ~5 times — elapsed reflects wall clock.
    expect(result.current).toBeGreaterThanOrEqual(500)
    expect(result.current).toBeLessThan(600)
  })

  it('stops ticking when active flips to false', () => {
    const start = Date.now()
    const { result, rerender } = renderHook(
      ({ active }: { active: boolean }) => useElapsedMs(start, active),
      { initialProps: { active: true } },
    )

    act(() => {
      vi.advanceTimersByTime(300)
    })
    const frozen = result.current

    rerender({ active: false })

    act(() => {
      vi.advanceTimersByTime(1000)
    })
    // No new ticks scheduled — value should be the last sampled one.
    expect(result.current).toBe(frozen)
  })

  it('returns 0 when startMs is undefined', () => {
    const { result } = renderHook(() => useElapsedMs(undefined, true))
    expect(result.current).toBe(0)
  })
})
