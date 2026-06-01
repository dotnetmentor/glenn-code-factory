// Filter for unhandled promise rejections that are known-harmless byproducts
// of an in-flight RPC being cancelled. Lives next to `main.ts`'s top-level
// `process.on('unhandledRejection')` handler — that handler used to crash the
// daemon on every user-initiated turn cancel because @cursor/sdk →
// @connectrpc/connect-node leaks an orphaned `ConnectError [canceled]`
// rejection from its internal HTTP/2 streaming RPC.
//
// === Observed crash (2026-05-26, runtime 3bda26a5…58) ===
//
// User cancels a turn → daemon calls `run.cancel()` → SDK fires
// `AbortController.abort()` on its in-flight ConnectRPC request → the
// underlying request promise inside the connect-node universal client
// rejects synchronously from the abort listener:
//
//   ConnectError: [canceled] This operation was aborted
//     at ConnectError.from        (@connectrpc/connect/.../connect-error.js)
//     at connectErrorFromNodeReason (@connectrpc/connect-node/.../node-error.js)
//     at Object.reject             (@connectrpc/connect-node/.../node-universal-client.js)
//     at AbortSignal.onSignalAbort (@connectrpc/connect-node/.../node-universal-client.js)
//     at AbortController.abort     (node:internal/abort_controller)
//
// No `.catch()` is attached to that promise (the SDK doesn't expose it; the
// `for await ... of run.stream()` consumer has already drained), so Node
// escalates it to `unhandledRejection` and our crash handler kills the
// daemon. Heartbeat watcher then marks the runtime Crashed, respawn rotates
// the Fly machine — every cancel = guaranteed runtime crash.
//
// The real fix is inside the SDK (`.catch(() => {})` on the internal
// request promise when cancel is invoked). We don't own that code, so we
// filter at the top of the process: recognize the canonical shape of these
// rejections and swallow them, leaving every OTHER rejection on the crash
// path. Conservative — matches only documented ConnectError-canceled and
// Node AbortError shapes.

/**
 * Return true if a rejection reason is a documented "benign cancel" — i.e.
 * the orphaned downstream of an explicit AbortController.abort() on an
 * in-flight RPC. These should be logged and swallowed; they are NOT real
 * daemon faults and crashing on them is what produced the
 * 2026-05-26 cancel-kills-runtime regression.
 *
 * Matches:
 *  - `ConnectError` with `code === Code.Canceled (=1)` or `code === 'canceled'`
 *  - `ConnectError` whose message starts with `[canceled]` (cross-version
 *    safety in case the `code` field renames or moves)
 *  - DOMException-shaped `AbortError` (Node 16+ native AbortController)
 *  - One-level-deep `cause.name === 'AbortError'` (libraries that wrap)
 *
 * Returns false for null, undefined, primitives, and any other rejection
 * shape — those must keep flowing to the crash handler.
 */
export function isBenignAbortRejection(reason: unknown): boolean {
  if (reason === null || reason === undefined) return false
  if (typeof reason !== 'object') return false
  const err = reason as {
    name?: unknown
    code?: unknown
    message?: unknown
    cause?: unknown
  }
  // ConnectRPC ConnectError — match by name + (numeric or string) code, or
  // by the canonical `[canceled]` message prefix as a cross-version fallback.
  if (err.name === 'ConnectError') {
    if (err.code === 1 || err.code === 'canceled') return true
    if (
      typeof err.message === 'string' &&
      /^\[canceled\]/i.test(err.message)
    ) {
      return true
    }
  }
  // Native Node AbortError (DOMException-shaped).
  if (err.name === 'AbortError') return true
  // Wrapped: some libs hide AbortError under .cause.
  const cause = err.cause
  if (cause !== null && cause !== undefined && typeof cause === 'object') {
    const causeName = (cause as { name?: unknown }).name
    if (causeName === 'AbortError') return true
  }
  return false
}
