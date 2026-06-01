/**
 * preview-bridge — bidirectional comms between the preview iframe and the
 * hosting studio window.
 *
 * Error reporting is dual-channel: every runtime/console/rejection/boundary
 * error is forwarded to BOTH
 *   1. window.parent.postMessage (for the studio UI overlay), and
 *   2. errorReporting.captureError (for persistence to the backend).
 *
 * Each channel is isolated in its own try/catch so one channel failing cannot
 * block the other. The postMessage payload shape is unchanged from previous
 * versions — existing studio listeners continue to work without modification.
 *
 * No side effects on import — listeners are installed only when init() runs.
 * init() is idempotent; calling it twice is a no-op.
 */

import { captureError, startFlushing } from './errorReporting'

type ErrorSource =
  | 'runtime'
  | 'unhandledRejection'
  | 'console'
  | 'errorBoundary'

interface PreviewErrorPayload {
  message: string
  stack?: string
  filename?: string
  lineno?: number
  colno?: number
  source: ErrorSource
}

let initialized = false
let errorBuffer: PreviewErrorPayload[] = []
let errorFlushTimeout: ReturnType<typeof setTimeout> | null = null
const DEBOUNCE_MS = 500

// Originals we patch — captured so we can restore on __resetForTests.
let originalConsoleError: typeof console.error | undefined
let originalFetch: typeof window.fetch | undefined
let originalPushState: typeof history.pushState | undefined
let originalReplaceState: typeof history.replaceState | undefined

// Listener references so we can remove them cleanly.
const listeners: Array<{
  target: EventTarget
  type: string
  handler: EventListenerOrEventListenerObject
}> = []

function sendToStudio(type: string, payload?: unknown) {
  // Isolated try/catch — postMessage failure must never block captureError.
  try {
    window.parent.postMessage({ type, payload, timestamp: Date.now() }, '*')
  } catch {
    // Swallow: postMessage channel is best-effort.
  }
}

function queuePostMessageError(error: PreviewErrorPayload) {
  errorBuffer.push(error)

  if (!errorFlushTimeout) {
    errorFlushTimeout = setTimeout(() => {
      const snapshot = errorBuffer
      errorBuffer = []
      errorFlushTimeout = null
      for (const e of snapshot) {
        sendToStudio('preview:error', e)
      }
    }, DEBOUNCE_MS)
  }
}

/**
 * Dual-channel dispatch. Each channel is wrapped in its own try/catch so a
 * failure in one NEVER prevents the other from firing. Duplicate-call
 * prevention: each caller invokes reportError() exactly once per event, and
 * this function forwards to each channel exactly once per call.
 */
function reportError(
  payload: PreviewErrorPayload,
  rawError: unknown,
  context?: Parameters<typeof captureError>[1],
) {
  // Channel 1: postMessage (debounced via queue).
  try {
    queuePostMessageError(payload)
  } catch {
    // Never throw out of error handlers.
  }

  // Channel 2: captureError → backend.
  try {
    captureError(rawError, context)
  } catch {
    // Never throw out of error handlers.
  }
}

export function reportErrorBoundary(error: Error, componentStack?: string) {
  reportError(
    {
      message: error.message,
      stack: error.stack,
      filename: componentStack || undefined,
      source: 'errorBoundary',
    },
    error,
    { errorType: 'ErrorBoundary' },
  )
}

export function notifyReady() {
  sendToStudio('preview:ready')
}

// ---------- init / teardown ----------

export function init(): void {
  if (initialized) return
  initialized = true

  // Wire up backend flush infrastructure first. Uses its own idempotence so
  // that if errorReporting.init() has already run (unlikely in practice),
  // startFlushing is a no-op.
  try {
    startFlushing()
  } catch {
    // Never throw during init — backend channel failure must not break the
    // postMessage channel.
  }

  // Runtime errors — use addEventListener so we catch BOTH browser-dispatched
  // uncaught errors and test-dispatched ErrorEvents.
  const onError = (evt: Event) => {
    const e = evt as ErrorEvent
    const error = e.error as Error | undefined
    const message = e.message
    const filename = e.filename
    const lineno = e.lineno
    const colno = e.colno

    reportError(
      {
        message: String(message),
        stack: error?.stack,
        filename: filename || undefined,
        lineno: lineno || undefined,
        colno: colno || undefined,
        source: 'runtime',
      },
      error ?? (typeof message === 'string' ? message : String(message)),
      {
        lineNumber: typeof lineno === 'number' ? lineno : undefined,
        columnNumber: typeof colno === 'number' ? colno : undefined,
        url: typeof filename === 'string' ? filename : undefined,
      },
    )
  }
  addTrackedListener(window, 'error', onError as EventListener)

  const onUnhandledRejection = (evt: Event) => {
    const e = evt as PromiseRejectionEvent
    const reason = e.reason
    reportError(
      {
        message: (reason && (reason as Error).message) || String(reason),
        stack: (reason as Error | undefined)?.stack,
        source: 'unhandledRejection',
      },
      reason,
    )
  }
  addTrackedListener(
    window,
    'unhandledrejection',
    onUnhandledRejection as EventListener,
  )

  // console.error override. Preserve original behaviour AND dual-channel report.
  originalConsoleError = console.error
  console.error = (...args: unknown[]) => {
    const message = args
      .map((a) => (typeof a === 'object' ? safeStringify(a) : String(a)))
      .join(' ')

    // If the first arg is an Error, prefer it as the "raw error" for backend.
    const firstErr = args.find((a) => a instanceof Error) as Error | undefined

    reportError(
      {
        message,
        stack: firstErr?.stack,
        source: 'console',
      },
      firstErr ?? message,
      { errorType: 'ConsoleError' },
    )

    if (typeof originalConsoleError === 'function') {
      try {
        originalConsoleError.apply(console, args)
      } catch {
        // ignore
      }
    }
  }

  // Fetch instrumentation — unchanged payload shape, postMessage only.
  // Network failures are a separate concern from runtime errors; captureError's
  // own patched fetch (when errorReporting.init() is used) handles those. We
  // don't double-report here.
  originalFetch = window.fetch
  window.fetch = async (input, init) => {
    const url =
      typeof input === 'string'
        ? input
        : input instanceof Request
          ? input.url
          : String(input)
    const method = init?.method || 'GET'

    let requestBody: unknown
    try {
      if (init?.body) {
        requestBody =
          typeof init.body === 'string' ? JSON.parse(init.body) : init.body
      }
    } catch {
      requestBody = init?.body
    }

    try {
      const response = await (originalFetch as typeof window.fetch)(
        input,
        init,
      )

      if (!response.ok) {
        const clonedResponse = response.clone()
        let responseBody: unknown

        try {
          const text = await clonedResponse.text()
          try {
            responseBody = JSON.parse(text)
          } catch {
            responseBody = text
          }
        } catch {
          responseBody = null
        }

        sendToStudio('preview:networkError', {
          url,
          method,
          status: response.status,
          statusText: response.statusText,
          requestBody,
          responseBody,
        })
      }

      return response
    } catch (error) {
      sendToStudio('preview:networkError', {
        url,
        method,
        requestBody,
        error: error instanceof Error ? error.message : String(error),
      })
      throw error
    }
  }

  // Route-change tracking.
  let lastPath = window.location.pathname
  const checkRouteChange = () => {
    if (window.location.pathname !== lastPath) {
      lastPath = window.location.pathname
      sendToStudio('preview:routeChange', {
        path: lastPath,
        fullUrl: window.location.href,
      })
    }
  }

  originalPushState = history.pushState
  originalReplaceState = history.replaceState

  history.pushState = function (...args) {
    ;(originalPushState as typeof history.pushState).apply(this, args)
    checkRouteChange()
  }

  history.replaceState = function (...args) {
    ;(originalReplaceState as typeof history.replaceState).apply(this, args)
    checkRouteChange()
  }

  addTrackedListener(window, 'popstate', checkRouteChange as EventListener)

  addTrackedListener(window, 'message', ((event: MessageEvent) => {
    const data = (event.data ?? {}) as {
      type?: string
      payload?: { path?: string }
    }
    const { type, payload } = data

    switch (type) {
      case 'studio:reload':
        window.location.reload()
        break
      case 'studio:navigate':
        if (payload?.path) {
          window.history.pushState({}, '', payload.path)
          window.dispatchEvent(new PopStateEvent('popstate'))
        }
        break
      case 'studio:ping':
        sendToStudio('preview:pong')
        break
    }
  }) as EventListener)
}

function addTrackedListener(
  target: EventTarget,
  type: string,
  handler: EventListener,
) {
  target.addEventListener(type, handler)
  listeners.push({ target, type, handler })
}

function safeStringify(value: unknown): string {
  try {
    return JSON.stringify(value)
  } catch {
    return String(value)
  }
}

/**
 * @internal Testing helper — tears down all listeners/patches and resets
 * module state so tests can re-init. Not part of the public contract.
 */
export function __resetForTests(): void {
  // Restore patched globals.
  if (originalConsoleError) {
    console.error = originalConsoleError
    originalConsoleError = undefined
  }
  if (originalFetch) {
    window.fetch = originalFetch
    originalFetch = undefined
  }
  if (originalPushState) {
    history.pushState = originalPushState
    originalPushState = undefined
  }
  if (originalReplaceState) {
    history.replaceState = originalReplaceState
    originalReplaceState = undefined
  }

  // Remove tracked listeners.
  for (const { target, type, handler } of listeners) {
    try {
      target.removeEventListener(type, handler)
    } catch {
      // ignore
    }
  }
  listeners.length = 0

  // Clear buffer state.
  if (errorFlushTimeout) {
    clearTimeout(errorFlushTimeout)
    errorFlushTimeout = null
  }
  errorBuffer = []
  initialized = false
}
