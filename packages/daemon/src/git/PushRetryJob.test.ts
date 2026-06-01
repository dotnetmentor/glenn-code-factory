// Tests for PushRetryJob. Same pattern as the sibling tests:
// hand-rolled fakes (no `vi.mock`), pino-shaped logger stub, vitest fake timers.
//
// The QuietModeManager fake is a plain Node EventEmitter — PushRetryJob only
// reaches for `on`/`off` on the `sleep` and `wake` events, so EventEmitter is
// the smallest fake that satisfies the contract.
//
// Cadence assertions: we drive the clock with `vi.advanceTimersByTimeAsync`
// and count tick-side effects (gitModule.push call counts) rather than
// inspecting timer internals — same approach HeartbeatModule.test uses.

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { PushRetryJob, type PushRetryGitModule } from './PushRetryJob.js'

// ============================================================================
// Test helpers
// ============================================================================

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log
}

interface PushResult {
  ok: boolean
  authError?: boolean
  conflict?: boolean
  outputTail?: string
}

function makeGitModuleStub() {
  const push = vi.fn<
    (remote: string, branch: string) => Promise<PushResult>
  >(async () => ({ ok: true }))
  const stub: PushRetryGitModule = { push }
  return { stub, push }
}

function makeSignalrStub() {
  const emitEvent = vi.fn(async (_p: EmitEventPayload) => {})
  const stub = { emitEvent } as unknown as Pick<SignalRClient, 'emitEvent'>
  return { stub, emitEvent }
}

interface BuildOpts {
  intervalMs?: number
  quietIntervalMs?: number
  maxAttempts?: number
}

function build(opts: BuildOpts = {}) {
  const log = makeLogger()
  const git = makeGitModuleStub()
  const signalr = makeSignalrStub()
  const quietMode = new EventEmitter()
  const job = new PushRetryJob({
    gitModule: git.stub,
    signalr: signalr.stub,
    quietMode,
    logger: log as unknown as import('pino').Logger,
    intervalMs: opts.intervalMs ?? 30_000,
    quietIntervalMs: opts.quietIntervalMs ?? 300_000,
    maxAttempts: opts.maxAttempts ?? 5,
  })
  return { job, log, gitPush: git.push, emitEvent: signalr.emitEvent, quietMode }
}

// ============================================================================
// Tests
// ============================================================================

describe('PushRetryJob', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  describe('recordFailure', () => {
    it('de-duplicates by remote+branch — two failures for the same branch yield a single entry', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000, maxAttempts: 5 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.recordFailure('origin', 'main')
      job.start()

      // First tick: one push for origin/main (the entry already had
      // attemptCount=2 from the second recordFailure; this tick takes it to
      // 3 — still under maxAttempts so no exhaustion yet).
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)
      expect(gitPush.mock.calls[0]).toEqual(['origin', 'main'])

      job.stop()
    })

    it('keeps separate entries for different branches', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.recordFailure('origin', 'feature/x')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)

      // Both branches retried in this tick.
      expect(gitPush).toHaveBeenCalledTimes(2)
      const calls = gitPush.mock.calls.map((c) => `${c[0]}/${c[1]}`).sort()
      expect(calls).toEqual(['origin/feature/x', 'origin/main'])

      job.stop()
    })

    it('keeps separate entries when the same branch is on two different remotes', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.recordFailure('upstream', 'main')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      job.stop()
    })
  })

  describe('retry behaviour', () => {
    it('removes the entry on a successful retry', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValueOnce({ ok: true })

      job.recordFailure('origin', 'main')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Next tick: nothing to do, no further pushes.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      job.stop()
    })

    it('logs info on successful retry', async () => {
      const { job, log, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValueOnce({ ok: true })

      job.recordFailure('origin', 'main')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)

      const successLog = log.info.mock.calls.find(
        (call) => typeof call[1] === 'string' && call[1].includes('push retry succeeded'),
      )
      expect(successLog).toBeDefined()

      job.stop()
    })

    it('increments attemptCount on a failed retry, keeps the entry', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000, maxAttempts: 5 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Entry still pending — next tick retries again.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(3)

      job.stop()
    })

    it('emits exhausted event and removes the entry after maxAttempts retries', async () => {
      const { job, gitPush, emitEvent } = build({
        intervalMs: 30_000,
        maxAttempts: 5,
      })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main') // attemptCount = 1
      job.start()

      // Each tick increments attemptCount by 1. We need attemptCount to reach
      // 5 — with starting value 1, that's 4 ticks (1 → 2, 2 → 3, 3 → 4, 4 → 5).
      await vi.advanceTimersByTimeAsync(30_000) // 1 → 2
      await vi.advanceTimersByTimeAsync(30_000) // 2 → 3
      await vi.advanceTimersByTimeAsync(30_000) // 3 → 4
      expect(emitEvent).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(30_000) // 4 → 5 → exhausted
      expect(gitPush).toHaveBeenCalledTimes(4)
      expect(emitEvent).toHaveBeenCalledTimes(1)

      // Subsequent ticks: entry is gone, no more pushes.
      await vi.advanceTimersByTimeAsync(30_000)
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(4)

      job.stop()
    })

    it('exhausted event payload carries the expected fields', async () => {
      const { job, gitPush, emitEvent } = build({
        intervalMs: 30_000,
        maxAttempts: 2, // one retry, then exhausted
      })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main') // attemptCount = 1
      job.start()

      await vi.advanceTimersByTimeAsync(30_000) // 1 → 2 → exhausted

      expect(emitEvent).toHaveBeenCalledTimes(1)
      const payload = emitEvent.mock.calls[0]?.[0] as EmitEventPayload
      expect(payload.sessionId).toBe('')
      expect(payload.kind).toBe('Status')
      const data = JSON.parse(payload.eventData)
      expect(data.type).toBe('git_push_exhausted')
      expect(data.remote).toBe('origin')
      expect(data.branch).toBe('main')
      expect(data.attempts).toBe(2)
      expect(typeof data.text).toBe('string')
      expect(data.text).toContain('origin/main')
      expect(data.text).toContain('2 attempts')

      job.stop()
    })

    it('fast-tracks auth errors to exhaustion (one retry then exhausted)', async () => {
      const { job, gitPush, emitEvent } = build({
        intervalMs: 30_000,
        maxAttempts: 5,
      })
      gitPush.mockResolvedValue({ ok: false, authError: true })

      job.recordFailure('origin', 'main') // attemptCount = 1
      job.start()

      // First tick: push attempted, returns authError → fast-track to
      // attemptCount = maxAttempts (5) → exhausted on this very tick.
      await vi.advanceTimersByTimeAsync(30_000)

      expect(gitPush).toHaveBeenCalledTimes(1)
      expect(emitEvent).toHaveBeenCalledTimes(1)
      const payload = emitEvent.mock.calls[0]?.[0] as EmitEventPayload
      const data = JSON.parse(payload.eventData)
      expect(data.attempts).toBe(5)

      // Subsequent ticks: entry is gone.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      job.stop()
    })

    it('does not crash when emitEvent rejects on exhaustion (logs error instead)', async () => {
      const { job, gitPush, emitEvent, log } = build({
        intervalMs: 30_000,
        maxAttempts: 2,
      })
      gitPush.mockResolvedValue({ ok: false })
      emitEvent.mockRejectedValue(new Error('hub down'))

      job.recordFailure('origin', 'main')
      job.start()

      // 1 → 2 → exhausted. emitEvent rejects but the loop must keep going.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(emitEvent).toHaveBeenCalledTimes(1)

      // Microtask flush so the awaited .catch on emitEvent runs.
      await Promise.resolve()
      await Promise.resolve()
      const errored = log.error.mock.calls.some(
        (c) => typeof c[1] === 'string' && c[1].includes('git_push_exhausted'),
      )
      expect(errored).toBe(true)

      // Loop still alive.
      job.recordFailure('origin', 'feature/y')
      await vi.advanceTimersByTimeAsync(30_000)
      // The new branch was retried (we count: first tick for main → 1 push,
      // second tick for the new feature → 1 more push).
      expect(gitPush).toHaveBeenCalledTimes(2)

      job.stop()
    })

    it('treats a thrown push() as a failed attempt and keeps ticking', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000, maxAttempts: 5 })
      gitPush.mockRejectedValueOnce(new Error('boom'))
      gitPush.mockResolvedValue({ ok: true })

      job.recordFailure('origin', 'main')
      job.start()

      // First tick: thrown → counts as failed → attemptCount → 2, entry kept.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Second tick: ok → entry removed.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      // Third tick: nothing to do.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      job.stop()
    })
  })

  describe('quiet-mode awareness', () => {
    it('switches to quietIntervalMs on quiet:enter (sleep)', async () => {
      const { job, gitPush, quietMode } = build({
        intervalMs: 30_000,
        quietIntervalMs: 300_000,
      })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()

      // Fast cadence: tick at 30s.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Enter quiet mode.
      quietMode.emit('sleep')

      // Advance 30s — at the slow cadence, nothing should fire.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Advance to the slow-cadence boundary (300s total since the switch).
      await vi.advanceTimersByTimeAsync(270_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      job.stop()
    })

    it('switches back to fast intervalMs on quiet:exit (wake)', async () => {
      const { job, gitPush, quietMode } = build({
        intervalMs: 30_000,
        quietIntervalMs: 300_000,
      })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()

      // Go quiet immediately.
      quietMode.emit('sleep')

      // 30s passes — slow cadence, no tick.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(0)

      // Wake — back to fast cadence.
      quietMode.emit('wake')

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      // Confirm fast cadence keeps ticking.
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(2)

      job.stop()
    })

    it('a duplicate sleep event does not restart the timer (no extra tick churn)', async () => {
      const { job, gitPush, quietMode } = build({
        intervalMs: 30_000,
        quietIntervalMs: 300_000,
      })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()

      quietMode.emit('sleep')
      // Halfway into the slow interval — a second sleep should NOT reset the
      // schedule (otherwise the operator would see ticks delayed by the
      // duplicate event).
      await vi.advanceTimersByTimeAsync(150_000)
      quietMode.emit('sleep')
      await vi.advanceTimersByTimeAsync(150_000)

      // First tick should land at the original 300s mark.
      expect(gitPush).toHaveBeenCalledTimes(1)

      job.stop()
    })
  })

  describe('lifecycle', () => {
    it('start() is idempotent', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()
      job.start() // second call: must not double-schedule
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)
      // Exactly one tick, not three.
      expect(gitPush).toHaveBeenCalledTimes(1)

      job.stop()
    })

    it('stop() is idempotent', () => {
      const { job } = build({ intervalMs: 30_000 })
      job.start()
      expect(() => job.stop()).not.toThrow()
      expect(() => job.stop()).not.toThrow()
      expect(() => job.stop()).not.toThrow()
    })

    it('stop() before start() is a no-op', () => {
      const { job } = build({ intervalMs: 30_000 })
      expect(() => job.stop()).not.toThrow()
    })

    it('stop() clears the interval — no further pushes', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      gitPush.mockResolvedValue({ ok: false })

      job.recordFailure('origin', 'main')
      job.start()

      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).toHaveBeenCalledTimes(1)

      job.stop()

      await vi.advanceTimersByTimeAsync(60_000 * 5)
      expect(gitPush).toHaveBeenCalledTimes(1)
    })

    it('stop() detaches sleep/wake listeners (post-stop events do nothing)', async () => {
      const { job, gitPush, quietMode } = build({
        intervalMs: 30_000,
        quietIntervalMs: 300_000,
      })
      gitPush.mockResolvedValue({ ok: false })

      job.start()
      job.stop()

      // After stop, listeners must be gone — emitting sleep/wake should not
      // throw and should not start a new timer.
      expect(() => quietMode.emit('sleep')).not.toThrow()
      expect(() => quietMode.emit('wake')).not.toThrow()

      // Confirm no timer is running by recording a failure and advancing time.
      job.recordFailure('origin', 'main')
      await vi.advanceTimersByTimeAsync(60_000 * 10)
      expect(gitPush).toHaveBeenCalledTimes(0)

      // listenerCount should be 0 on both events.
      expect(quietMode.listenerCount('sleep')).toBe(0)
      expect(quietMode.listenerCount('wake')).toBe(0)
    })

    it('does nothing on tick when there are no pending entries', async () => {
      const { job, gitPush } = build({ intervalMs: 30_000 })
      job.start()
      await vi.advanceTimersByTimeAsync(30_000)
      await vi.advanceTimersByTimeAsync(30_000)
      expect(gitPush).not.toHaveBeenCalled()
      job.stop()
    })
  })
})
