// Locks in the cancel-doesn't-crash-the-daemon fix for the 2026-05-26
// regression where every user-initiated turn cancel killed the runtime. See
// the module header in `./isBenignAbortRejection.ts` for the full incident
// narrative.
//
// Tests are deliberately literal — we hand-build error objects whose shape
// matches what @cursor/sdk + @connectrpc/connect-node throw at runtime,
// rather than depending on the live packages (we don't want the test to
// silently start failing on an SDK upgrade; the predicate's contract is
// what we own, not the SDK's internal types).

import { describe, expect, it } from 'vitest'

import { isBenignAbortRejection } from './isBenignAbortRejection.js'

describe('isBenignAbortRejection', () => {
  describe('returns true for benign rejections', () => {
    it('ConnectError with numeric Code.Canceled (=1)', () => {
      // Shape produced by @connectrpc/connect's ConnectError class — what
      // we observed in production on 2026-05-26.
      const err = Object.assign(new Error('[canceled] This operation was aborted'), {
        name: 'ConnectError',
        code: 1,
      })
      expect(isBenignAbortRejection(err)).toBe(true)
    })

    it('ConnectError with string code "canceled"', () => {
      // Hypothetical future SDK shape — defensive cross-version match.
      const err = Object.assign(new Error('[canceled] This operation was aborted'), {
        name: 'ConnectError',
        code: 'canceled',
      })
      expect(isBenignAbortRejection(err)).toBe(true)
    })

    it('ConnectError matched only by [canceled] message prefix', () => {
      // No `code` property at all — fallback path for cross-version safety.
      const err = Object.assign(new Error('[canceled] This operation was aborted'), {
        name: 'ConnectError',
      })
      expect(isBenignAbortRejection(err)).toBe(true)
    })

    it('Node AbortError (DOMException-shaped)', () => {
      const err = Object.assign(new Error('The operation was aborted'), {
        name: 'AbortError',
      })
      expect(isBenignAbortRejection(err)).toBe(true)
    })

    it('library-wrapped error with cause.name === AbortError', () => {
      const innerAbort = Object.assign(new Error('aborted'), { name: 'AbortError' })
      const outer = Object.assign(new Error('Request failed'), {
        name: 'FetchError',
        cause: innerAbort,
      })
      expect(isBenignAbortRejection(outer)).toBe(true)
    })

    it('production stack — full ConnectError shape from logs', () => {
      // Verbatim reconstruction of the rejection we shipped on
      // 2026-05-26 14:57:47 → ReportError to RuntimeErrorReports.
      const err = Object.assign(new Error('[canceled] This operation was aborted'), {
        name: 'ConnectError',
        code: 1,
        cause: 'This operation was aborted',
      })
      expect(isBenignAbortRejection(err)).toBe(true)
    })
  })

  describe('returns false for non-benign rejections', () => {
    it('null reason', () => {
      expect(isBenignAbortRejection(null)).toBe(false)
    })

    it('undefined reason', () => {
      expect(isBenignAbortRejection(undefined)).toBe(false)
    })

    it('primitive string (Promise.reject("oops") path)', () => {
      // Some callers reject with a plain string — must NOT be swallowed.
      expect(isBenignAbortRejection('something failed')).toBe(false)
    })

    it('primitive number', () => {
      expect(isBenignAbortRejection(42)).toBe(false)
    })

    it('generic Error', () => {
      expect(isBenignAbortRejection(new Error('something went wrong'))).toBe(false)
    })

    it('ConnectError with non-canceled code (Unavailable=14)', () => {
      const err = Object.assign(new Error('[unavailable] backend down'), {
        name: 'ConnectError',
        code: 14,
      })
      expect(isBenignAbortRejection(err)).toBe(false)
    })

    it('TypeError that happens to mention canceled', () => {
      // Name-mismatch guard — we filter on .name, not on free-form message
      // content alone. A bug labelled "canceled" should still crash.
      const err = new TypeError('Cannot cancel: x is undefined')
      expect(isBenignAbortRejection(err)).toBe(false)
    })

    it('cause that is not an Error-like object', () => {
      const err = Object.assign(new Error('boom'), {
        name: 'CustomError',
        cause: 'AbortError',  // string, not object — must not match
      })
      expect(isBenignAbortRejection(err)).toBe(false)
    })

    it('SDK billing/auth error — must propagate to crash handler', () => {
      // The category of error we MUST keep crashing on. The header comment
      // in main.ts's crash-capture block specifically calls these out.
      const err = Object.assign(new Error('Payment required'), {
        name: 'ConnectError',
        code: 16, // Code.Unauthenticated
      })
      expect(isBenignAbortRejection(err)).toBe(false)
    })
  })
})
