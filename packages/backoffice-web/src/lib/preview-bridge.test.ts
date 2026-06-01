import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// We mock errorReporting BEFORE importing preview-bridge so the bridge picks
// up our spies. captureError + startFlushing are the two exports the bridge
// should consume.
vi.mock('./errorReporting', () => ({
  startFlushing: vi.fn(),
  captureError: vi.fn(),
  // Keep a no-op init so any accidental import path works in tests.
  init: vi.fn(),
}))

import * as errorReporting from './errorReporting'
import {
  __resetForTests as resetBridge,
  init as initPreviewBridge,
  reportErrorBoundary,
} from './preview-bridge'

type Mock = ReturnType<typeof vi.fn>

function installParentPostMessageMock(): Mock {
  const postMessageMock = vi.fn()
  // happy-dom treats window.parent as window itself by default. Override so we
  // can observe postMessage calls in isolation.
  Object.defineProperty(window, 'parent', {
    configurable: true,
    get: () =>
      ({
        postMessage: postMessageMock,
      }) as unknown as Window,
  })
  return postMessageMock
}

describe('preview-bridge — dual-channel error reporting', () => {
  let postMessageMock: Mock
  let captureErrorMock: Mock
  let startFlushingMock: Mock

  beforeEach(() => {
    vi.useFakeTimers()
    postMessageMock = installParentPostMessageMock()
    captureErrorMock = errorReporting.captureError as unknown as Mock
    startFlushingMock = errorReporting.startFlushing as unknown as Mock
    captureErrorMock.mockReset()
    startFlushingMock.mockReset()
  })

  afterEach(() => {
    resetBridge()
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('init() calls startFlushing exactly once to wire flush infrastructure', () => {
    initPreviewBridge()
    expect(startFlushingMock).toHaveBeenCalledTimes(1)

    // Idempotent — calling init() twice does not re-wire.
    initPreviewBridge()
    expect(startFlushingMock).toHaveBeenCalledTimes(1)
  })

  it('window.onerror triggers BOTH postMessage AND captureError', () => {
    initPreviewBridge()

    const err = new Error('boom')
    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'boom',
        error: err,
        filename: 'https://app/foo.js',
        lineno: 42,
        colno: 7,
      }),
    )

    // postMessage is debounced via setTimeout in preview-bridge's queue —
    // advance past DEBOUNCE_MS to flush.
    vi.advanceTimersByTime(1000)

    expect(postMessageMock).toHaveBeenCalledTimes(1)
    const [payload] = postMessageMock.mock.calls[0]
    expect(payload.type).toBe('preview:error')
    expect(payload.payload.source).toBe('runtime')

    // captureError is called synchronously (not debounced) — once per event.
    expect(captureErrorMock).toHaveBeenCalledTimes(1)
    const [capturedErr, capturedContext] = captureErrorMock.mock.calls[0]
    // The handler should pass along the real Error so errorReporting can
    // extract the stack + name.
    expect(capturedErr).toBeInstanceOf(Error)
    expect((capturedErr as Error).message).toBe('boom')
    expect(capturedContext).toMatchObject({
      lineNumber: 42,
      columnNumber: 7,
    })
  })

  it('unhandledrejection triggers BOTH channels', () => {
    initPreviewBridge()

    const rejectionError = new Error('rejected!')
    const event = new Event('unhandledrejection') as Event & {
      reason: unknown
      promise: Promise<unknown>
    }
    ;(event as unknown as { reason: unknown }).reason = rejectionError
    ;(event as unknown as { promise: Promise<unknown> }).promise =
      Promise.reject(rejectionError).catch(() => undefined)
    window.dispatchEvent(event)

    vi.advanceTimersByTime(1000)

    expect(postMessageMock).toHaveBeenCalledTimes(1)
    const [payload] = postMessageMock.mock.calls[0]
    expect(payload.type).toBe('preview:error')
    expect(payload.payload.source).toBe('unhandledRejection')

    expect(captureErrorMock).toHaveBeenCalledTimes(1)
    const [capturedErr] = captureErrorMock.mock.calls[0]
    expect(capturedErr).toBe(rejectionError)
  })

  it('reportErrorBoundary triggers BOTH channels', () => {
    initPreviewBridge()

    const boundaryErr = new Error('boundary-bad')
    reportErrorBoundary(boundaryErr, 'at <App>')

    vi.advanceTimersByTime(1000)

    expect(postMessageMock).toHaveBeenCalledTimes(1)
    const [payload] = postMessageMock.mock.calls[0]
    expect(payload.payload.source).toBe('errorBoundary')

    expect(captureErrorMock).toHaveBeenCalledTimes(1)
    expect(captureErrorMock.mock.calls[0][0]).toBe(boundaryErr)
  })

  it('if captureError throws, postMessage is still called', () => {
    captureErrorMock.mockImplementation(() => {
      throw new Error('capture-exploded')
    })

    initPreviewBridge()

    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'still-works',
        error: new Error('still-works'),
      }),
    )

    vi.advanceTimersByTime(1000)

    // captureError failure must NOT block the postMessage channel.
    expect(postMessageMock).toHaveBeenCalledTimes(1)
    expect(captureErrorMock).toHaveBeenCalledTimes(1)
  })

  it('if postMessage throws, captureError is still called', () => {
    postMessageMock.mockImplementation(() => {
      throw new Error('postMessage-exploded')
    })

    initPreviewBridge()

    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'still-captures',
        error: new Error('still-captures'),
      }),
    )

    vi.advanceTimersByTime(1000)

    // postMessage failure must NOT block the captureError channel.
    expect(captureErrorMock).toHaveBeenCalledTimes(1)
    // We still attempted postMessage before it threw.
    expect(postMessageMock).toHaveBeenCalledTimes(1)
  })

  it('a single error event does not produce duplicates in either channel', () => {
    initPreviewBridge()

    window.dispatchEvent(
      new ErrorEvent('error', {
        message: 'once',
        error: new Error('once'),
      }),
    )

    vi.advanceTimersByTime(1000)

    expect(postMessageMock).toHaveBeenCalledTimes(1)
    expect(captureErrorMock).toHaveBeenCalledTimes(1)
  })

  it('console.error override still fires each channel exactly once per call', () => {
    initPreviewBridge()

    console.error('something bad')

    vi.advanceTimersByTime(1000)

    expect(postMessageMock).toHaveBeenCalledTimes(1)
    const [payload] = postMessageMock.mock.calls[0]
    expect(payload.payload.source).toBe('console')

    expect(captureErrorMock).toHaveBeenCalledTimes(1)
  })
})
