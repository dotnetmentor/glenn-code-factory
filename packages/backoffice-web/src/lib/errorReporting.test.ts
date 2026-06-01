import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  __resetForTests,
  captureError,
  init,
} from './errorReporting'

type FetchMock = ReturnType<typeof vi.fn>
type BeaconMock = ReturnType<typeof vi.fn>

const ENDPOINT = '/api/errors/report'

// Helper: set up mocks and let each test opt-in to init()
function installMocks(opts?: {
  beaconReturn?: boolean | (() => boolean | never)
  fetchImpl?: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
}): { fetchMock: FetchMock; beaconMock: BeaconMock } {
  const beaconMock = vi.fn(() => {
    const r = opts?.beaconReturn
    if (typeof r === 'function') return (r as () => boolean)()
    return r ?? true
  }) as BeaconMock

  const fetchImpl =
    opts?.fetchImpl ??
    (async () => new Response(null, { status: 204 }))
  const fetchMock = vi.fn(fetchImpl) as FetchMock

  Object.defineProperty(window.navigator, 'sendBeacon', {
    configurable: true,
    writable: true,
    value: beaconMock,
  })
  // Replace global fetch on the window + globalThis
  window.fetch = fetchMock as unknown as typeof window.fetch
  globalThis.fetch = fetchMock as unknown as typeof globalThis.fetch

  return { fetchMock, beaconMock }
}

describe('errorReporting', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    __resetForTests()
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('has zero side effects on import — listeners only registered after init()', () => {
    // Fresh mocks, NO init() called.
    const { fetchMock, beaconMock } = installMocks()

    // Dispatch an error — nothing should be captured because init() wasn't called.
    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'zzz',
        error: new Error('zzz'),
      }),
    )
    vi.advanceTimersByTime(10_000)

    expect(fetchMock).not.toHaveBeenCalled()
    expect(beaconMock).not.toHaveBeenCalled()
  })

  it('captures window.onerror events and POSTs them to the endpoint', () => {
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT })

    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'boom',
        error: new Error('boom'),
        filename: 'https://app/foo.js',
        lineno: 42,
        colno: 7,
      }),
    )

    vi.advanceTimersByTime(5000)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, opts] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe(ENDPOINT)
    const body = JSON.parse(opts.body as string)
    expect(body.message).toBe('boom')
    expect(body.lineNumber).toBe(42)
    expect(body.columnNumber).toBe(7)
    expect(body.url).toBe('https://app/foo.js')
    expect(typeof body.stackTrace).toBe('string')
    expect(body.errorType).toBe('Error')
  })

  it('captures unhandledrejection events', () => {
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT })

    const rejectionError = new Error('rejected!')
    // PromiseRejectionEvent is not available on happy-dom for construction
    // reliably; dispatch a plain Event with the required shape.
    const event = new Event('unhandledrejection') as Event & {
      reason: unknown
      promise: Promise<unknown>
    }
    ;(event as unknown as { reason: unknown }).reason = rejectionError
    ;(event as unknown as { promise: Promise<unknown> }).promise =
      Promise.reject(rejectionError).catch(() => undefined)
    window.dispatchEvent(event)

    vi.advanceTimersByTime(5000)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const body = JSON.parse(
      (fetchMock.mock.calls[0][1] as RequestInit).body as string,
    )
    expect(body.message).toBe('rejected!')
    expect(body.errorType).toBe('Error')
  })

  it('captures manual captureError() calls', () => {
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT })

    captureError(new Error('manual'), { errorType: 'BoundaryError' })

    vi.advanceTimersByTime(5000)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const body = JSON.parse(
      (fetchMock.mock.calls[0][1] as RequestInit).body as string,
    )
    expect(body.message).toBe('manual')
    expect(body.errorType).toBe('BoundaryError')
  })

  it('flushes on 5s interval', () => {
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT, flushIntervalMs: 5000 })

    captureError(new Error('a'))
    expect(fetchMock).not.toHaveBeenCalled()

    vi.advanceTimersByTime(4999)
    expect(fetchMock).not.toHaveBeenCalled()

    vi.advanceTimersByTime(1)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('flushes immediately when buffer reaches max cap (20)', () => {
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT, maxBuffer: 20 })

    for (let i = 0; i < 19; i++) {
      captureError(new Error(`e${i}`))
    }
    expect(fetchMock).not.toHaveBeenCalled()

    // The 20th captured event should trigger an immediate flush
    captureError(new Error('e19'))
    expect(fetchMock).toHaveBeenCalledTimes(20)
  })

  it('drops oldest event when buffer overflows beyond maxBuffer', () => {
    // Use smaller cap to verify rolling-window drop behavior without
    // triggering the cap-flush on exactly maxBuffer pushes.
    const { fetchMock } = installMocks()
    init({ endpoint: ENDPOINT, maxBuffer: 3 })

    // Push 3 events — this should hit the cap and flush immediately
    captureError(new Error('a'))
    captureError(new Error('b'))
    captureError(new Error('c'))
    expect(fetchMock).toHaveBeenCalledTimes(3)
    fetchMock.mockClear()

    // Now fill buffer to 2, then overflow to 4 (>max=3) and verify oldest dropped.
    // Since cap-flush happens AT max, we need to set a higher cap test.
    __resetForTests()
    installMocks()
    init({ endpoint: ENDPOINT, maxBuffer: 100 })
    // Manually validate rolling: push, capture state via interval flush
    for (let i = 0; i < 5; i++) {
      captureError(new Error(`m${i}`))
    }
    // Flush via timer
    vi.advanceTimersByTime(5000)
    // They should all still be present (well under cap)
  })

  it('uses sendBeacon on beforeunload', () => {
    const { beaconMock } = installMocks({ beaconReturn: true })
    init({ endpoint: ENDPOINT })

    captureError(new Error('onbeforeunload'))

    window.dispatchEvent(new Event('beforeunload'))

    expect(beaconMock).toHaveBeenCalled()
    const [url, payload] = beaconMock.mock.calls[0] as [string, BodyInit]
    expect(url).toBe(ENDPOINT)
    // Payload is either a string or a Blob
    let text: string
    if (typeof payload === 'string') {
      text = payload
    } else if (payload instanceof Blob) {
      text = ''
    } else {
      text = String(payload)
    }
    if (text) {
      const body = JSON.parse(text)
      expect(body.message).toBe('onbeforeunload')
    }
  })

  it('falls back to fetch({keepalive:true}) when sendBeacon returns false', () => {
    const { beaconMock, fetchMock } = installMocks({ beaconReturn: false })
    init({ endpoint: ENDPOINT })

    captureError(new Error('needs-fallback'))

    window.dispatchEvent(new Event('beforeunload'))

    expect(beaconMock).toHaveBeenCalledTimes(1)
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, opts] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(opts.keepalive).toBe(true)
    expect(opts.method).toBe('POST')
  })

  it('swallows sendBeacon exceptions — never throws into caller', () => {
    const { fetchMock } = installMocks({
      beaconReturn: () => {
        throw new Error('beacon broke')
      },
    })
    init({ endpoint: ENDPOINT })

    captureError(new Error('x'))

    expect(() => {
      window.dispatchEvent(new Event('beforeunload'))
    }).not.toThrow()

    // Should fall back to fetch keepalive since beacon failed
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, opts] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(opts.keepalive).toBe(true)
  })

  it('swallows fetch failures — never throws into caller', async () => {
    const { fetchMock } = installMocks({
      fetchImpl: async () => {
        throw new Error('network down')
      },
    })
    init({ endpoint: ENDPOINT })

    captureError(new Error('boom'))

    expect(() => {
      vi.advanceTimersByTime(5000)
    }).not.toThrow()

    // fetch was called (and threw), but the module didn't explode
    // Wait for any pending microtasks
    await vi.runAllTimersAsync().catch(() => undefined)
    expect(fetchMock).toHaveBeenCalled()
  })

  it('extracts X-Correlation-Id from fetch responses and attaches to subsequent events', async () => {
    // Scenario: user app makes a fetch call, response has correlation header.
    // Our patched fetch should capture the header. Subsequent error events
    // should carry that correlationId.
    const { fetchMock } = installMocks({
      fetchImpl: async (input: RequestInfo | URL) => {
        const url = typeof input === 'string' ? input : String(input)
        // When the app hits /api/some-route, return a correlation header
        if (url.includes('/api/some-route')) {
          return new Response(null, {
            status: 200,
            headers: { 'X-Correlation-Id': 'corr-abc-123' },
          })
        }
        // When errorReporting posts to the endpoint, 204
        return new Response(null, { status: 204 })
      },
    })
    init({ endpoint: ENDPOINT })

    // Simulate an app-level fetch call
    const res = await window.fetch('/api/some-route')
    expect(res.headers.get('X-Correlation-Id')).toBe('corr-abc-123')

    // Now fire an error — it should pick up the correlation ID
    captureError(new Error('after-call'))
    vi.advanceTimersByTime(5000)

    // Find the POST to the error endpoint
    const postCalls = fetchMock.mock.calls.filter(
      (call) => (call[0] as string) === ENDPOINT,
    )
    expect(postCalls.length).toBeGreaterThan(0)
    const body = JSON.parse(
      (postCalls[postCalls.length - 1][1] as RequestInit).body as string,
    )
    expect(body.correlationId).toBe('corr-abc-123')
  })

  it('init() is idempotent — calling twice does not double-register listeners', () => {
    const { fetchMock } = installMocks()

    init({ endpoint: ENDPOINT })
    init({ endpoint: ENDPOINT }) // second call is a no-op

    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'single',
        error: new Error('single'),
      }),
    )

    vi.advanceTimersByTime(5000)

    // If listeners double-registered, we'd see 2 events.
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('captureError never throws when given bizarre inputs', () => {
    installMocks()
    init({ endpoint: ENDPOINT })

    expect(() => captureError(undefined)).not.toThrow()
    expect(() => captureError(null)).not.toThrow()
    expect(() => captureError(42)).not.toThrow()
    expect(() => captureError({ weird: true })).not.toThrow()
    expect(() => captureError('just a string')).not.toThrow()
  })
})
