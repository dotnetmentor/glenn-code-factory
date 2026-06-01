/**
 * errorReporting — frontend error capture client.
 *
 * Responsibilities:
 *  - Capture window.onerror, unhandledrejection, and manual captureError() calls.
 *  - Buffer events in memory (max 20, rolling window) and ship them to the
 *    backend POST /api/errors/report endpoint.
 *  - Flush triggers: buffer cap (20), 5s interval, and beforeunload.
 *  - On beforeunload: use navigator.sendBeacon; fall back to fetch({keepalive:true})
 *    if sendBeacon is unavailable, returns false, or throws.
 *  - Read `X-Correlation-Id` (or `traceparent`) from any fetch response and
 *    attach the last-seen value to subsequent buffered events in the same page
 *    lifecycle.
 *  - Never throw into caller code. Every handler is wrapped in try/catch.
 *  - init() is idempotent — calling twice is a no-op on the second call.
 *
 * No side effects on import — listeners are installed only when init() runs.
 *
 * Known limitation: the backend endpoint accepts ONE error per POST, so a
 * flush of 20 events fires 20 individual requests (or 20 sendBeacon calls on
 * beforeunload). If this becomes a perf or browser-connection-limit issue, the
 * backend should add a batch endpoint and this module should send a single
 * beacon with the full array. Tracked as a follow-up.
 */

export interface ErrorReportEvent {
  message: string
  stackTrace?: string
  url?: string
  userAgent?: string
  correlationId?: string
  errorType?: string
  lineNumber?: number
  columnNumber?: number
}

export interface ErrorReportingConfig {
  endpoint?: string
  maxBuffer?: number
  flushIntervalMs?: number
}

type FlushReason = 'interval' | 'cap' | 'beforeunload' | 'manual'

interface ResolvedConfig {
  endpoint: string
  maxBuffer: number
  flushIntervalMs: number
}

let initialized = false
let buffer: ErrorReportEvent[] = []
let lastCorrelationId: string | undefined
let flushTimer: ReturnType<typeof setInterval> | undefined
let config: ResolvedConfig = {
  endpoint: '/api/errors/report',
  maxBuffer: 20,
  flushIntervalMs: 5000,
}
let originalFetch: typeof window.fetch | undefined

// ---------- public API ----------

export function init(userConfig: ErrorReportingConfig = {}): void {
  if (initialized) return
  initialized = true

  config = {
    endpoint: userConfig.endpoint ?? '/api/errors/report',
    maxBuffer: userConfig.maxBuffer ?? 20,
    flushIntervalMs: userConfig.flushIntervalMs ?? 5000,
  }

  try {
    window.addEventListener('error', onWindowError)
    window.addEventListener('unhandledrejection', onUnhandledRejection)
    window.addEventListener('beforeunload', onBeforeUnload)
  } catch {
    // Never throw during init
  }

  try {
    flushTimer = setInterval(() => {
      flush('interval')
    }, config.flushIntervalMs)
  } catch {
    // Never throw during init
  }

  try {
    patchFetch()
  } catch {
    // Never throw during init
  }
}

/**
 * startFlushing — a lighter-weight subset of init() intended for callers that
 * already own their own error listeners (e.g. preview-bridge) and just want the
 * flush-to-backend infrastructure wired up. Sets up:
 *  - config resolution
 *  - setInterval flush timer
 *  - beforeunload beacon flush
 * Does NOT register window.error / unhandledrejection listeners and does NOT
 * patch fetch — the caller is responsible for invoking captureError() on the
 * events it cares about.
 *
 * Idempotent — both init() and startFlushing() share the same `initialized`
 * flag. Calling startFlushing() after init() (or vice versa) is a no-op.
 */
export function startFlushing(userConfig: ErrorReportingConfig = {}): void {
  if (initialized) return
  initialized = true

  config = {
    endpoint: userConfig.endpoint ?? '/api/errors/report',
    maxBuffer: userConfig.maxBuffer ?? 20,
    flushIntervalMs: userConfig.flushIntervalMs ?? 5000,
  }

  try {
    window.addEventListener('beforeunload', onBeforeUnload)
  } catch {
    // Never throw during init
  }

  try {
    flushTimer = setInterval(() => {
      flush('interval')
    }, config.flushIntervalMs)
  } catch {
    // Never throw during init
  }
}

export function captureError(
  err: unknown,
  context?: Partial<ErrorReportEvent>,
): void {
  if (!initialized) return
  try {
    const event = toEvent(err, context)
    addToBuffer(event)
  } catch {
    // Never throw into caller
  }
}

/**
 * flushNow — force an immediate flush of any buffered events.
 * Normally the flush is driven by the 5-second interval + the rolling buffer
 * cap. This escape hatch is intended for:
 *   - Acceptance tests that want deterministic delivery without waiting for
 *     the next interval tick.
 *   - Pages that know they're about to navigate and want to ship errors
 *     before the navigation (beforeunload does this automatically, but
 *     explicit flush is occasionally useful).
 *
 * Returns silently if the module is not yet initialized. Never throws.
 */
export function flushNow(): void {
  if (!initialized) return
  try {
    flush('manual')
  } catch {
    // Never throw into caller
  }
}

/**
 * @internal Testing helper — unregisters listeners, clears buffers/timers,
 * restores original fetch, and resets module state so tests can re-init.
 * Not part of the public contract.
 */
export function __resetForTests(): void {
  try {
    window.removeEventListener('error', onWindowError)
    window.removeEventListener('unhandledrejection', onUnhandledRejection)
    window.removeEventListener('beforeunload', onBeforeUnload)
  } catch {
    // ignore
  }
  if (flushTimer !== undefined) {
    clearInterval(flushTimer)
    flushTimer = undefined
  }
  if (originalFetch) {
    try {
      window.fetch = originalFetch
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      ;(globalThis as any).fetch = originalFetch
    } catch {
      // ignore
    }
    originalFetch = undefined
  }
  buffer = []
  lastCorrelationId = undefined
  initialized = false
  config = {
    endpoint: '/api/errors/report',
    maxBuffer: 20,
    flushIntervalMs: 5000,
  }
}

// ---------- internals ----------

function toEvent(
  err: unknown,
  context?: Partial<ErrorReportEvent>,
): ErrorReportEvent {
  let message = 'Unknown error'
  let stackTrace: string | undefined
  let errorType: string | undefined

  if (err instanceof Error) {
    message = err.message || err.name || 'Error'
    stackTrace = err.stack
    errorType = err.name
  } else if (typeof err === 'string') {
    message = err
  } else if (err === null) {
    message = 'null'
  } else if (err === undefined) {
    message = 'undefined'
  } else {
    try {
      message = JSON.stringify(err)
    } catch {
      message = String(err)
    }
  }

  // Cap message length so we don't exceed the backend's 1000-char limit
  if (message.length > 1000) message = message.slice(0, 1000)
  if (stackTrace && stackTrace.length > 4000)
    stackTrace = stackTrace.slice(0, 4000)

  const base: ErrorReportEvent = {
    message,
    stackTrace,
    errorType,
    url: safeHref(),
    userAgent: safeUserAgent(),
    correlationId: lastCorrelationId,
  }

  // Context wins (e.g. ErrorEvent line/col/filename overrides)
  if (context) {
    for (const key of Object.keys(context) as (keyof ErrorReportEvent)[]) {
      const v = context[key]
      if (v !== undefined) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        ;(base as any)[key] = v
      }
    }
  }

  return base
}

function safeHref(): string | undefined {
  try {
    return window.location.href
  } catch {
    return undefined
  }
}

function safeUserAgent(): string | undefined {
  try {
    return navigator.userAgent
  } catch {
    return undefined
  }
}

function addToBuffer(event: ErrorReportEvent): void {
  buffer.push(event)

  // Rolling window: if we've exceeded the cap by more than one, drop oldest
  while (buffer.length > config.maxBuffer) {
    buffer.shift()
  }

  if (buffer.length >= config.maxBuffer) {
    flush('cap')
  }
}

function flush(reason: FlushReason): void {
  if (buffer.length === 0) return

  const snapshot = buffer
  buffer = []

  for (const event of snapshot) {
    if (reason === 'beforeunload') {
      shipViaBeacon(event)
    } else {
      shipViaFetch(event, false)
    }
  }
}

function shipViaBeacon(event: ErrorReportEvent): void {
  try {
    const body = JSON.stringify(event)
    const beacon =
      typeof navigator !== 'undefined' &&
      typeof navigator.sendBeacon === 'function'
        ? navigator.sendBeacon.bind(navigator)
        : undefined

    if (beacon) {
      let ok = false
      try {
        // Many browsers expect a Blob with an explicit content type.
        // sendBeacon returns false if the user agent refuses to queue it.
        const blob =
          typeof Blob !== 'undefined'
            ? new Blob([body], { type: 'application/json' })
            : body
        ok = beacon(config.endpoint, blob as BodyInit)
      } catch {
        ok = false
      }

      if (ok) return
    }

    // Fallback: fetch with keepalive. Works during unload in most browsers.
    shipViaFetch(event, true)
  } catch {
    // Never throw
  }
}

function shipViaFetch(event: ErrorReportEvent, keepalive: boolean): void {
  try {
    const fetchFn = originalFetch ?? window.fetch
    if (!fetchFn) return
    const body = JSON.stringify(event)
    // Fire and forget. Do NOT await — we don't want to block the flush loop
    // or the unload handler. Any rejection is swallowed.
    const p = fetchFn(config.endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
      keepalive,
      credentials: 'same-origin',
    })
    // Attach a rejection handler so we never produce an unhandled rejection.
    if (p && typeof (p as Promise<Response>).then === 'function') {
      ;(p as Promise<Response>).catch(() => undefined)
    }
  } catch {
    // Never throw
  }
}

function patchFetch(): void {
  if (typeof window.fetch !== 'function') return
  originalFetch = window.fetch.bind(window)

  const wrapped: typeof window.fetch = async (input, init) => {
    // Guard: never instrument our own outbound error reports (avoid loops)
    try {
      const urlStr =
        typeof input === 'string'
          ? input
          : input instanceof URL
            ? input.toString()
            : input instanceof Request
              ? input.url
              : ''
      if (urlStr && config.endpoint && urlStr === config.endpoint) {
        return await (originalFetch as typeof window.fetch)(input, init)
      }
    } catch {
      // fall through — just call original
    }

    try {
      const res = await (originalFetch as typeof window.fetch)(input, init)
      try {
        extractCorrelationId(res)
      } catch {
        // ignore
      }
      return res
    } catch (err) {
      // Capture network failures but rethrow so callers see the real error.
      try {
        captureError(err, { errorType: 'NetworkError' })
      } catch {
        // ignore
      }
      throw err
    }
  }

  window.fetch = wrapped
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  ;(globalThis as any).fetch = wrapped
}

function extractCorrelationId(res: Response): void {
  try {
    const id =
      res.headers.get('X-Correlation-Id') ??
      res.headers.get('x-correlation-id') ??
      res.headers.get('traceparent')
    if (id) {
      lastCorrelationId = id.slice(0, 100)
    }
  } catch {
    // ignore
  }
}

// ---------- event handlers ----------

function onWindowError(evt: Event): void {
  try {
    const e = evt as ErrorEvent
    const err = e.error ?? e.message ?? 'Script error'
    captureError(err, {
      lineNumber: typeof e.lineno === 'number' ? e.lineno : undefined,
      columnNumber: typeof e.colno === 'number' ? e.colno : undefined,
      url: e.filename || safeHref(),
    })
  } catch {
    // Never throw
  }
}

function onUnhandledRejection(evt: Event): void {
  try {
    const e = evt as PromiseRejectionEvent
    captureError(e.reason)
  } catch {
    // Never throw
  }
}

function onBeforeUnload(): void {
  try {
    flush('beforeunload')
  } catch {
    // Never throw
  }
}
