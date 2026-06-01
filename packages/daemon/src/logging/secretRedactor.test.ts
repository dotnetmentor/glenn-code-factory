// secretRedactor.test.ts — pure-function + pino-integration coverage for the
// daemon's secret scrubber. Mirrors
// packages/dotnet-api/Tests/Infrastructure/Logging/SecretValueRedactorTests.cs
// case-for-case so the parity stays auditable in code review.

import { Writable } from 'node:stream'
import pino from 'pino'
import { describe, expect, it } from 'vitest'

import {
  REDACTED,
  containsSecretShape,
  pinoSecretRedactionOptions,
  redactSecrets,
  withSecretRedaction,
} from './secretRedactor.js'

// ---------------------------------------------------------------------------
// Positive cases — each enabled regex matches at least one example.
// ---------------------------------------------------------------------------

describe('redactSecrets — positive cases', () => {
  it('scrubs Stripe live secret keys', () => {
    const input = 'Charge failed key=sk_live_abcdefghij1234567890XYZ end'
    const out = redactSecrets(input)
    expect(out).toContain(REDACTED)
    expect(out).not.toContain('sk_live_abcdefghij1234567890XYZ')
  })

  it('scrubs Stripe test secret keys', () => {
    const input = 'Using sk_test_abcdefghij1234567890XYZ to charge.'
    const out = redactSecrets(input)
    expect(out).not.toContain('sk_test_abcdefghij1234567890XYZ')
    expect(out).toContain(REDACTED)
  })

  it('scrubs Stripe publishable keys', () => {
    const input = 'frontend uses pk_live_abcdefghij1234567890ABCDEF'
    const out = redactSecrets(input)
    expect(out).not.toContain('pk_live_abcdefghij1234567890ABCDEF')
    expect(out).toContain(REDACTED)
  })

  it('scrubs Bearer tokens (case-sensitive)', () => {
    const input = 'Authorization: Bearer abcdefghij1234567890ZZZZZ done'
    const out = redactSecrets(input)
    expect(out).not.toContain('abcdefghij1234567890ZZZZZ')
    expect(out).toContain(REDACTED)
  })

  it('scrubs Bearer tokens regardless of case', () => {
    const input = 'authorization: bearer abcdefghij1234567890ZZZZZ'
    const out = redactSecrets(input)
    expect(out).not.toContain('abcdefghij1234567890ZZZZZ')
  })

  it('scrubs AWS access keys', () => {
    const input = 'uploading via AKIAIOSFODNN7EXAMPLE to bucket'
    const out = redactSecrets(input)
    expect(out).not.toContain('AKIAIOSFODNN7EXAMPLE')
    expect(out).toContain(REDACTED)
  })

  it('scrubs OpenAI keys', () => {
    const input = 'key=sk-abcdefghij1234567890XYZab end'
    const out = redactSecrets(input)
    expect(out).not.toContain('sk-abcdefghij1234567890XYZab')
    expect(out).toContain(REDACTED)
  })

  it('scrubs OpenAI project-scoped keys', () => {
    const input = 'OPENAI_API_KEY=sk-proj-abcdefghij1234567890XYZab'
    const out = redactSecrets(input)
    expect(out).not.toContain('sk-proj-abcdefghij1234567890XYZab')
    expect(out).toContain(REDACTED)
  })

  it('scrubs every match when multiple secrets share a line', () => {
    const input =
      'stripe=sk_live_abcdefghij1234567890XYZ aws=AKIAIOSFODNN7EXAMPLE'
    const out = redactSecrets(input)
    expect(out).not.toContain('sk_live_abcdefghij1234567890XYZ')
    expect(out).not.toContain('AKIAIOSFODNN7EXAMPLE')
    // Two distinct redactions present.
    expect(out.split(REDACTED).length).toBe(3)
  })
})

// ---------------------------------------------------------------------------
// Negative cases — strings that look secret-shaped but aren't.
// ---------------------------------------------------------------------------

describe('redactSecrets — negative cases', () => {
  it('does not redact UUIDs', () => {
    // UUID hyphens split it into 8/4/4/4/12-char runs; none are 40+ alnum
    // and none match a provider prefix. Default set excludes
    // GENERIC_HIGH_ENTROPY entirely so the string is untouched.
    const input = 'request_id=550e8400-e29b-41d4-a716-446655440000 succeeded'
    expect(redactSecrets(input)).toBe(input)
  })

  it('does not redact normal English log messages', () => {
    const input = 'User alice@example.com logged in at 12:34:56'
    expect(redactSecrets(input)).toBe(input)
  })

  it('does not redact ISO timestamps', () => {
    const input = 'event at 2026-05-09T12:34:56.789Z processed'
    expect(redactSecrets(input)).toBe(input)
  })

  it('does not redact short stripe-like placeholders below the 20-char minimum', () => {
    const input = 'example: sk_test_short'
    expect(redactSecrets(input)).toBe(input)
  })

  it('returns empty string for empty input', () => {
    expect(redactSecrets('')).toBe('')
  })

  it('returns empty string for non-string input', () => {
    // The signature is string but pino's streamWrite always passes a string;
    // a defensive call with non-string still doesn't throw.
    expect(redactSecrets(null as unknown as string)).toBe('')
    expect(redactSecrets(undefined as unknown as string)).toBe('')
  })
})

// ---------------------------------------------------------------------------
// containsSecretShape — quick membership check.
// ---------------------------------------------------------------------------

describe('containsSecretShape', () => {
  it('returns true for Stripe keys', () => {
    expect(
      containsSecretShape('prefix sk_live_abcdefghij1234567890XYZ suffix'),
    ).toBe(true)
  })

  it('returns false for plain text', () => {
    expect(containsSecretShape('nothing to see here')).toBe(false)
  })

  it('returns false for empty / non-string input', () => {
    expect(containsSecretShape('')).toBe(false)
    expect(containsSecretShape(null as unknown as string)).toBe(false)
    expect(containsSecretShape(undefined as unknown as string)).toBe(false)
  })
})

// ---------------------------------------------------------------------------
// GENERIC_HIGH_ENTROPY opt-in — disabled by default.
// ---------------------------------------------------------------------------

describe('redactSecrets — GENERIC_HIGH_ENTROPY opt-in', () => {
  it('scrubs long opaque strings when the caller opts in', () => {
    const input =
      'token=abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx done'
    const out = redactSecrets(input, { includeHighEntropy: true })
    expect(out).toContain(REDACTED)
    expect(out).not.toContain(
      'abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx',
    )
  })

  it('passes long opaque strings through by default', () => {
    const input =
      'build_hash=abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx'
    expect(redactSecrets(input)).toBe(input)
  })
})

// ---------------------------------------------------------------------------
// Pino integration — wire pinoSecretRedactionOptions() into a real logger
// pointed at a writable stream and assert the captured JSON line is scrubbed.
// ---------------------------------------------------------------------------

/**
 * Captures every chunk written into a writable into a string array — used as
 * the destination for the pino instance under test. We use this rather than
 * mocking pino because the streamWrite hook only fires on the actual write
 * path; mocking the logger would skip the hook entirely.
 */
class CaptureStream extends Writable {
  public chunks: string[] = []
  override _write(
    chunk: Buffer | string,
    _enc: BufferEncoding,
    cb: (err?: Error | null) => void,
  ): void {
    this.chunks.push(typeof chunk === 'string' ? chunk : chunk.toString('utf8'))
    cb()
  }
  text(): string {
    return this.chunks.join('')
  }
}

describe('pino integration', () => {
  it('redacts a Stripe key emitted via a structured property', () => {
    const sink = new CaptureStream()
    const logger = pino(
      { level: 'info', ...pinoSecretRedactionOptions() },
      sink,
    )
    logger.info(
      { key: 'sk_test_abcdef1234567890abcdefghij' },
      'paying with stripe',
    )

    const text = sink.text()
    expect(text).toContain(REDACTED)
    expect(text).not.toContain('sk_test_abcdef1234567890abcdefghij')
  })

  it('redacts a secret embedded in the log message string itself', () => {
    const sink = new CaptureStream()
    const logger = pino(
      { level: 'info', ...pinoSecretRedactionOptions() },
      sink,
    )
    logger.info('charging with sk_live_abcdefghij1234567890XYZ now')

    const text = sink.text()
    expect(text).toContain(REDACTED)
    expect(text).not.toContain('sk_live_abcdefghij1234567890XYZ')
  })

  it('passes non-secret messages through untouched', () => {
    const sink = new CaptureStream()
    const logger = pino(
      { level: 'info', ...pinoSecretRedactionOptions() },
      sink,
    )
    logger.info({ user: 'alice' }, 'user logged in')

    const text = sink.text()
    expect(text).toContain('user logged in')
    expect(text).toContain('alice')
    expect(text).not.toContain(REDACTED)
  })

  it('scrubs Bearer tokens written into a structured field', () => {
    const sink = new CaptureStream()
    const logger = pino(
      { level: 'info', ...pinoSecretRedactionOptions() },
      sink,
    )
    logger.info(
      { authorization: 'Bearer abcdefghij1234567890ZZZZZ' },
      'outbound request',
    )

    const text = sink.text()
    expect(text).not.toContain('abcdefghij1234567890ZZZZZ')
    expect(text).toContain(REDACTED)
  })
})

// ---------------------------------------------------------------------------
// withSecretRedaction — preserves a caller-supplied streamWrite.
// ---------------------------------------------------------------------------

describe('withSecretRedaction', () => {
  it('runs the existing streamWrite hook after redaction', () => {
    const sink = new CaptureStream()
    let userHookCalled = false
    const logger = pino(
      withSecretRedaction({
        level: 'info',
        hooks: {
          streamWrite: (s: string): string => {
            userHookCalled = true
            return s
          },
        },
      }),
      sink,
    )

    logger.info('paying with sk_live_abcdefghij1234567890XYZ now')

    expect(userHookCalled).toBe(true)
    const text = sink.text()
    expect(text).toContain(REDACTED)
    expect(text).not.toContain('sk_live_abcdefghij1234567890XYZ')
  })
})
