// Tests for BootstrapOrchestrator + the two stub stages. Same pattern as the
// sibling modules: hand-rolled SignalRClient stub + pino-shaped logger stub,
// vitest fake timers to deterministically drive the backoff schedule.
//
// NOTE on AbortSignal-between-stages: the orchestrator returns cleanly without
// throwing when the signal is aborted *between* stages — that's a clean
// shutdown, not a bootstrap failure. It throws BootstrapAbortedError when the
// signal aborts *during* a backoff wait, because the in-flight stage never
// completed.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonConfig } from '../config/DaemonConfig.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'

import {
  BootstrapAbortedError,
  BootstrapOrchestrator,
  MAX_BOOT_ATTEMPTS,
  MAX_SPEC_STAGE_RETRIES,
  type BootstrapStage,
  type BootstrapStageResult,
} from './BootstrapOrchestrator.js'
import { BootIssueStore } from './BootIssueStore.js'
import { VerifyEnvStage } from './stages/VerifyEnvStage.js'
import { ReportReadyStage } from './stages/ReportReadyStage.js'

// node:fs/promises is mocked for VerifyEnvStage tests at the bottom. Using
// vi.mock at module top is the only way that mock is hoisted correctly.
vi.mock('node:fs/promises', () => ({
  access: vi.fn(),
}))
import { access } from 'node:fs/promises'

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

function makeConfig(): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
  })
}

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

function makeSignalrStub() {
  const emitEvent = vi.fn(async (_payload: EmitEventPayload) => {})
  const runtimeReady = vi.fn(async () => {})
  // reportSpecHealth (self-healing-runtime-specs, D1): the orchestrator calls
  // this just before ReportReady to publish Healthy/Degraded. Stubbed so the
  // degraded-path tests can assert on the report; it is best-effort in prod.
  const reportSpecHealth = vi.fn(
    async (_report: {
      health: 'Healthy' | 'Degraded'
      issues: ReadonlyArray<Record<string, unknown>>
      summary: string
    }) => {},
  )
  const stub = { emitEvent, runtimeReady, reportSpecHealth } as unknown as SignalRClient
  return { stub, emitEvent, runtimeReady, reportSpecHealth }
}

class FakeStage implements BootstrapStage {
  readonly name: string
  readonly results: BootstrapStageResult[]
  attempts = 0
  constructor(name: string, results: BootstrapStageResult[]) {
    this.name = name
    this.results = results
  }
  async run(): Promise<BootstrapStageResult> {
    const result = this.results[this.attempts] ?? this.results[this.results.length - 1]!
    this.attempts++
    return result
  }
}

class ThrowingStage implements BootstrapStage {
  readonly name: string
  attempts = 0
  readonly #throwsTimes: number
  constructor(name: string, throwsTimes: number) {
    this.name = name
    this.#throwsTimes = throwsTimes
  }
  async run(): Promise<BootstrapStageResult> {
    this.attempts++
    if (this.attempts <= this.#throwsTimes) {
      throw new Error('boom')
    }
    return { ok: true }
  }
}

/**
 * Drives the orchestrator across all its scheduled backoff waits. We can't
 * just `await orch.start(...)` because the backoffs are real-timer awaits; we
 * advance fake timers in chunks until the start() promise settles.
 */
async function runUntilSettled(promise: Promise<void>): Promise<
  { ok: true } | { ok: false; err: unknown }
> {
  let settled = false
  let outcome: { ok: true } | { ok: false; err: unknown } | null = null
  promise.then(
    () => {
      settled = true
      outcome = { ok: true }
    },
    (err: unknown) => {
      settled = true
      outcome = { ok: false, err }
    },
  )

  // Generously advance through every possible backoff slot. BACKOFF_MS sums to
  // 1+2+4+8+30 = 45 seconds; advancing in 1s chunks up to 60 covers every
  // wakeup point and lets microtasks flush in between.
  for (let i = 0; i < 60 && !settled; i++) {
    await vi.advanceTimersByTimeAsync(1_000)
  }
  // Final microtask flush in case the last result resolves on a microtask
  // immediately after the timer fires.
  await Promise.resolve()
  await Promise.resolve()

  if (outcome === null) {
    throw new Error('runUntilSettled: promise never settled within budget')
  }
  return outcome
}

// ============================================================================
// Orchestrator tests
// ============================================================================

describe('BootstrapOrchestrator', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('runs all stages in order when all return ok and emits per-stage + completion events', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [{ ok: true }])
    const c = new FakeStage('c', [{ ok: true }])
    const { stub, emitEvent } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, b, c],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true)

    expect(a.attempts).toBe(1)
    expect(b.attempts).toBe(1)
    expect(c.attempts).toBe(1)

    // 3 stage_completed + 1 bootstrap_completed = 4 emits.
    expect(emitEvent).toHaveBeenCalledTimes(4)

    const eventTypes = emitEvent.mock.calls.map((call) => {
      const payload = call[0] as EmitEventPayload
      return JSON.parse(payload.eventData).type as string
    })
    expect(eventTypes).toEqual([
      'bootstrap_stage_completed',
      'bootstrap_stage_completed',
      'bootstrap_stage_completed',
      'bootstrap_completed',
    ])

    const stageNames = emitEvent.mock.calls.slice(0, 3).map((call) => {
      const payload = call[0] as EmitEventPayload
      return JSON.parse(payload.eventData).stage as string
    })
    expect(stageNames).toEqual(['a', 'b', 'c'])
  })

  it('retries a recoverable failure with backoff and eventually succeeds', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [
      { ok: false, reason: 'r1', recoverable: true },
      { ok: false, reason: 'r2', recoverable: true },
      { ok: true },
    ])
    const c = new FakeStage('c', [{ ok: true }])
    const { stub, emitEvent } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, b, c],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const startedAt = Date.now()
    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true)

    expect(b.attempts).toBe(3)
    expect(c.attempts).toBe(1)

    // Backoff schedule between attempts of b: BACKOFF_MS[0] (1000) before
    // attempt 2, BACKOFF_MS[1] (2000) before attempt 3 — total at least 3000.
    const elapsed = Date.now() - startedAt
    expect(elapsed).toBeGreaterThanOrEqual(3_000)

    // Event ordering: a-ok, b-fail, b-fail, b-ok, c-ok, completed.
    const events = emitEvent.mock.calls.map((call) => {
      const payload = call[0] as EmitEventPayload
      const data = JSON.parse(payload.eventData)
      return { type: data.type, stage: data.stage, ok: data.ok }
    })
    expect(events).toEqual([
      { type: 'bootstrap_stage_completed', stage: 'a', ok: true },
      { type: 'bootstrap_stage_completed', stage: 'b', ok: false },
      { type: 'bootstrap_stage_completed', stage: 'b', ok: false },
      { type: 'bootstrap_stage_completed', stage: 'b', ok: true },
      { type: 'bootstrap_stage_completed', stage: 'c', ok: true },
      { type: 'bootstrap_completed', stage: undefined, ok: undefined },
    ])
  })

  it('aborts immediately on unrecoverable failure (no retry, no later stages), marked terminal', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [{ ok: false, reason: 'fatal', recoverable: false }])
    const c = new FakeStage('c', [{ ok: true }])
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, b, c],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(false)
    expect((result as { ok: false; err: unknown }).err).toBeInstanceOf(BootstrapAbortedError)
    const err = (result as { ok: false; err: BootstrapAbortedError }).err
    expect(err.stage).toBe('b')
    expect(err.attempts).toBe(1)
    expect(err.reason).toBe('fatal')
    // recoverable:false is terminal — the .NET backend's
    // ScheduleRespawnHandler is responsible from here. main.ts pivots on
    // `.terminal` to decide whether to escalate via `sendErrorReport`.
    expect(err.terminal).toBe(true)

    expect(b.attempts).toBe(1)
    expect(c.attempts).toBe(0)
  })

  it('refuses to start when bootAttemptNumber exceeds the hard cap (terminal, no stages run)', async () => {
    // Same shape as production smoketest-mongo runaway: persistent counter
    // climbed past 100 because supervisord respawned the daemon on every
    // recoverable failure. The cap short-circuits before any stage runs.
    const a = new FakeStage('a', [{ ok: true }])
    const { stub, emitEvent } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      bootAttemptNumber: MAX_BOOT_ATTEMPTS + 1,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(false)
    const err = (result as { ok: false; err: BootstrapAbortedError }).err
    expect(err).toBeInstanceOf(BootstrapAbortedError)
    expect(err.stage).toBe('__bootstrap__')
    expect(err.terminal).toBe(true)
    expect(err.attempts).toBe(MAX_BOOT_ATTEMPTS + 1)
    expect(err.reason).toMatch(/boot-attempt cap exceeded/)

    // No stages ran and no progress events were emitted — the cap check
    // is the first thing `start()` does.
    expect(a.attempts).toBe(0)
    expect(emitEvent).not.toHaveBeenCalled()
  })

  it('runs normally when bootAttemptNumber is exactly at the cap (boundary)', async () => {
    // Boundary check: `MAX_BOOT_ATTEMPTS` itself is still allowed. The
    // cap fires on the FIRST attempt past it (this is the same semantic the
    // production logs would have hit at attempt 11).
    const a = new FakeStage('a', [{ ok: true }])
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      bootAttemptNumber: MAX_BOOT_ATTEMPTS,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true)
    expect(a.attempts).toBe(1)
  })

  it('aborts after MAX_ATTEMPTS recoverable failures (in-process retries exhausted, NOT terminal)', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [{ ok: false, reason: 'still broken', recoverable: true }])
    const c = new FakeStage('c', [{ ok: true }])
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, b, c],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(false)
    const err = (result as { ok: false; err: BootstrapAbortedError }).err
    expect(err).toBeInstanceOf(BootstrapAbortedError)
    expect(err.stage).toBe('b')
    expect(err.attempts).toBe(5)
    expect(err.reason).toBe('still broken')
    // Recoverable-but-exhausted within a single process is NOT terminal —
    // the next supervisord respawn may succeed if the environmental cause
    // has since cleared. The persistent boot-attempt cap is the second
    // safety net that catches a stage that keeps failing across respawns.
    expect(err.terminal).toBe(false)

    expect(b.attempts).toBe(5)
    expect(c.attempts).toBe(0)
  })

  it('treats a thrown error as recoverable and prefixes the reason with "threw:"', async () => {
    const a = new ThrowingStage('a', 1) // throws once, then succeeds
    const { stub, emitEvent } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true)
    expect(a.attempts).toBe(2)

    // First emit (the failing attempt) should carry reason starting with "threw:".
    const firstEvent = JSON.parse((emitEvent.mock.calls[0]?.[0] as EmitEventPayload).eventData)
    expect(firstEvent.ok).toBe(false)
    expect(firstEvent.reason).toMatch(/^threw: /)
    expect(firstEvent.reason).toContain('boom')
  })

  it('returns cleanly (does NOT throw) when AbortSignal aborts between stages', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [{ ok: true }])
    const ac = new AbortController()

    // Stage `a` aborts the signal as a side effect of its successful run, so
    // the orchestrator sees `signal.aborted` at the top of the loop before
    // running `b` and returns cleanly.
    class AbortingStage implements BootstrapStage {
      readonly name = 'aborting'
      attempts = 0
      async run(): Promise<BootstrapStageResult> {
        this.attempts++
        ac.abort()
        return { ok: true }
      }
    }

    const aborter = new AbortingStage()
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, aborter, b],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true) // Documented choice: clean return, not a throw.
    expect(a.attempts).toBe(1)
    expect(aborter.attempts).toBe(1)
    expect(b.attempts).toBe(0)
  })

  it('throws BootstrapAbortedError(reason="aborted") when signal aborts during backoff', async () => {
    const a = new FakeStage('a', [{ ok: false, reason: 'first', recoverable: true }])
    const ac = new AbortController()
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    // Kick off bootstrap; it'll fail attempt 1 and start waiting BACKOFF_MS[0]=1000ms.
    const promise = orch.start(ac.signal)
    let outcome: { ok: true } | { ok: false; err: unknown } | null = null
    promise.then(
      () => (outcome = { ok: true }),
      (err: unknown) => (outcome = { ok: false, err }),
    )

    // Let the first attempt run to completion (microtasks flush).
    await vi.advanceTimersByTimeAsync(0)
    expect(a.attempts).toBe(1)
    // Stage is now sleeping in backoff. Abort.
    ac.abort()
    // Drive timers forward enough to surface the AbortError from setTimeout.
    await vi.advanceTimersByTimeAsync(2_000)
    await Promise.resolve()
    await Promise.resolve()

    expect(outcome).not.toBeNull()
    const settledOutcome = outcome as unknown as { ok: false; err: BootstrapAbortedError }
    expect(settledOutcome.ok).toBe(false)
    const err = settledOutcome.err
    expect(err).toBeInstanceOf(BootstrapAbortedError)
    expect(err.reason).toBe('aborted')
    expect(err.stage).toBe('a')
    expect(err.attempts).toBe(1)
  })

  it('does NOT crash bootstrap when signalr.emitEvent rejects; logs error instead', async () => {
    const a = new FakeStage('a', [{ ok: true }])
    const b = new FakeStage('b', [{ ok: true }])
    const { stub, emitEvent } = makeSignalrStub()
    emitEvent.mockRejectedValue(new Error('hub down'))
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a, b],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))
    expect(result.ok).toBe(true)

    expect(a.attempts).toBe(1)
    expect(b.attempts).toBe(1)
    // 3 attempted emits (2 stages + completed), all rejecting.
    expect(emitEvent).toHaveBeenCalledTimes(3)
    // Each rejection logged.
    expect(log.error).toHaveBeenCalledTimes(3)
    expect(log.error.mock.calls[0]?.[1]).toMatch(/failed to emit bootstrap event/)
  })

  it('respects the BACKOFF_MS schedule (1s, 2s, 4s, 8s) across 4 retries', async () => {
    // Stage fails 4 times, then succeeds on attempt 5.
    const a = new FakeStage('a', [
      { ok: false, reason: 'r', recoverable: true },
      { ok: false, reason: 'r', recoverable: true },
      { ok: false, reason: 'r', recoverable: true },
      { ok: false, reason: 'r', recoverable: true },
      { ok: true },
    ])
    const { stub } = makeSignalrStub()
    const log = makeLogger()
    const orch = new BootstrapOrchestrator({
      stages: [a],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
    })

    const ac = new AbortController()
    const startedAt = Date.now()

    let settled = false
    let succeeded = false
    orch.start(ac.signal).then(
      () => {
        settled = true
        succeeded = true
      },
      () => {
        settled = true
      },
    )

    // After attempt 1 fails: sleep 1000.
    await vi.advanceTimersByTimeAsync(0) // attempt 1
    expect(a.attempts).toBe(1)
    expect(Date.now() - startedAt).toBeLessThan(1_000)

    await vi.advanceTimersByTimeAsync(1_000) // wait done, attempt 2
    expect(a.attempts).toBe(2)

    await vi.advanceTimersByTimeAsync(2_000) // attempt 3
    expect(a.attempts).toBe(3)

    await vi.advanceTimersByTimeAsync(4_000) // attempt 4
    expect(a.attempts).toBe(4)

    await vi.advanceTimersByTimeAsync(8_000) // attempt 5 (success)
    expect(a.attempts).toBe(5)

    await Promise.resolve()
    await Promise.resolve()
    expect(settled).toBe(true)
    expect(succeeded).toBe(true)
    // Total time advanced: 0 + 1000 + 2000 + 4000 + 8000 = 15000ms.
    expect(Date.now() - startedAt).toBe(15_000)
  })
})

// ============================================================================
// Degraded-online bootstrap (self-healing-runtime-specs, card D1)
// ============================================================================

describe('BootstrapOrchestrator — degraded-online path (D1)', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  /** A NON-CRITICAL (spec) stage: failures record a BootIssue, never abort. */
  class SpecStage implements BootstrapStage {
    readonly name: string
    readonly critical = false
    readonly #results: BootstrapStageResult[]
    attempts = 0
    constructor(name: string, results: BootstrapStageResult[]) {
      this.name = name
      this.#results = results
    }
    async run(): Promise<BootstrapStageResult> {
      const r = this.#results[this.attempts] ?? this.#results[this.#results.length - 1]!
      this.attempts++
      return r
    }
  }

  /** A stage named exactly 'report-ready' (the critical Online flip). */
  class FakeReportReady implements BootstrapStage {
    readonly name = 'report-ready'
    ran = false
    async run(): Promise<BootstrapStageResult> {
      this.ran = true
      return { ok: true }
    }
  }

  function makeEmitter() {
    const emit = vi.fn()
    const emitter = {
      emit,
      startTimer: vi.fn(() => ({ complete: vi.fn(), fail: vi.fn(), skip: vi.fn() })),
    }
    return { emitter, emit }
  }

  it('records a BootIssue, still reaches ReportReady, reports Degraded, and emits SpecDegraded', async () => {
    const ok = new FakeStage('writing-config', [{ ok: true }]) // critical, passes
    const bad = new SpecStage('install', [
      { ok: false, reason: 'install script exited 1: command not found', recoverable: true },
    ])
    const report = new FakeReportReady()
    const { stub, reportSpecHealth } = makeSignalrStub()
    const { emitter, emit } = makeEmitter()
    const bootIssues = new BootIssueStore()
    const log = makeLogger()

    const orch = new BootstrapOrchestrator({
      stages: [ok, bad, report],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      emitter: emitter as never,
      bootIssues,
    })

    const ac = new AbortController()
    const result = await runUntilSettled(orch.start(ac.signal))

    // Boot did NOT abort — the spec failure is data, not an exception.
    expect(result.ok).toBe(true)
    // The non-critical stage ran exactly once (no transient retries — the reason
    // is deterministic, not an infra flake).
    expect(bad.attempts).toBe(1)
    // ReportReady still ran → runtime reaches Online (degraded).
    expect(report.ran).toBe(true)

    // Exactly one BootIssue recorded for the failed spec stage.
    const issues = bootIssues.list()
    expect(issues).toHaveLength(1)
    expect(issues[0]!.stage).toBe('install')
    expect(issues[0]!.reason).toContain('command not found')

    // SpecHealth reported as Degraded, with the issue and a summary.
    expect(reportSpecHealth).toHaveBeenCalledTimes(1)
    const reportArg = reportSpecHealth.mock.calls[0]![0]
    expect(reportArg.health).toBe('Degraded')
    expect(reportArg.issues).toHaveLength(1)
    expect(reportArg.summary).toContain('install')

    // One SpecDegraded (Warn) RuntimeEvent emitted per boot issue.
    const degraded = emit.mock.calls.filter((c) => c[0] === 'SpecDegraded')
    expect(degraded).toHaveLength(1)
    expect(degraded[0]![1]).toBe('Warn')
    expect(degraded[0]![2]).toMatchObject({ stage: 'install' })
  })

  it('reports Healthy and emits no SpecDegraded when all spec stages pass', async () => {
    const good = new SpecStage('install', [{ ok: true }])
    const report = new FakeReportReady()
    const { stub, reportSpecHealth } = makeSignalrStub()
    const { emitter, emit } = makeEmitter()
    const bootIssues = new BootIssueStore()
    const log = makeLogger()

    const orch = new BootstrapOrchestrator({
      stages: [good, report],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      emitter: emitter as never,
      bootIssues,
    })

    const result = await runUntilSettled(orch.start(new AbortController().signal))
    expect(result.ok).toBe(true)
    expect(bootIssues.list()).toHaveLength(0)
    expect(reportSpecHealth).toHaveBeenCalledTimes(1)
    expect(reportSpecHealth.mock.calls[0]![0].health).toBe('Healthy')
    expect(emit.mock.calls.filter((c) => c[0] === 'SpecDegraded')).toHaveLength(0)
  })

  it('retries a TRANSIENT-looking spec failure within the bounded budget, then records degraded', async () => {
    // Fails every attempt with a transient (network) reason → exhausts the
    // MAX_SPEC_STAGE_RETRIES budget, then records ONE BootIssue and continues.
    const flaky = new SpecStage('install', [
      { ok: false, reason: 'failed to fetch: ETIMEDOUT contacting registry', recoverable: true },
    ])
    const report = new FakeReportReady()
    const { stub, reportSpecHealth } = makeSignalrStub()
    const { emitter } = makeEmitter()
    const bootIssues = new BootIssueStore()
    const log = makeLogger()

    const orch = new BootstrapOrchestrator({
      stages: [flaky, report],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      emitter: emitter as never,
      bootIssues,
    })

    const result = await runUntilSettled(orch.start(new AbortController().signal))
    expect(result.ok).toBe(true)
    // Ran the initial attempt + MAX_SPEC_STAGE_RETRIES retries.
    expect(flaky.attempts).toBe(MAX_SPEC_STAGE_RETRIES + 1)
    // Still only ONE BootIssue (transient budget exhausted → deterministic).
    expect(bootIssues.list()).toHaveLength(1)
    expect(reportSpecHealth.mock.calls[0]![0].health).toBe('Degraded')
  })

  it('does NOT abort the boot when a non-critical stage throws', async () => {
    const thrower = new SpecStage('running-setup', [])
    // Force a throw by overriding run.
    thrower.run = async () => {
      throw new Error('setup bash crashed')
    }
    const report = new FakeReportReady()
    const { stub, reportSpecHealth } = makeSignalrStub()
    const { emitter } = makeEmitter()
    const bootIssues = new BootIssueStore()
    const log = makeLogger()

    const orch = new BootstrapOrchestrator({
      stages: [thrower, report],
      signalr: stub,
      config: makeConfig(),
      logger: log as unknown as import('pino').Logger,
      emitter: emitter as never,
      bootIssues,
    })

    const result = await runUntilSettled(orch.start(new AbortController().signal))
    expect(result.ok).toBe(true)
    expect(report.ran).toBe(true)
    expect(bootIssues.list()).toHaveLength(1)
    expect(bootIssues.list()[0]!.reason).toContain('threw:')
    expect(reportSpecHealth.mock.calls[0]![0].health).toBe('Degraded')
  })
})

// ============================================================================
// VerifyEnvStage tests
// ============================================================================

describe('VerifyEnvStage', () => {
  const accessMock = vi.mocked(access)

  beforeEach(() => {
    accessMock.mockReset()
  })

  it('returns ok when the path exists', async () => {
    accessMock.mockResolvedValueOnce(undefined)
    const stage = new VerifyEnvStage({ path: '/data/project' })
    const result = await stage.run({} as never)
    expect(result.ok).toBe(true)
    expect(accessMock).toHaveBeenCalledWith('/data/project')
  })

  it('returns recoverable failure mentioning the path when it is missing', async () => {
    accessMock.mockRejectedValueOnce(new Error('ENOENT'))
    const stage = new VerifyEnvStage({ path: '/data/project' })
    const result = await stage.run({} as never)
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toContain('/data/project')
    }
  })

  it('defaults to /data/project when no path is given', async () => {
    accessMock.mockResolvedValueOnce(undefined)
    const stage = new VerifyEnvStage()
    expect(stage.name).toBe('verify-env')
    await stage.run({} as never)
    expect(accessMock).toHaveBeenCalledWith('/data/project')
  })
})

// ============================================================================
// ReportReadyStage tests
// ============================================================================

describe('ReportReadyStage', () => {
  it('calls runtimeReady() and returns ok on success', async () => {
    const stage = new ReportReadyStage()
    const { stub, runtimeReady } = makeSignalrStub()
    const result = await stage.run({
      config: makeConfig(),
      signalr: stub,
      logger: makeLogger() as unknown as import('pino').Logger,
      signal: new AbortController().signal,
    })
    expect(result.ok).toBe(true)
    expect(runtimeReady).toHaveBeenCalledTimes(1)
  })

  it('returns recoverable failure carrying the error message when runtimeReady throws', async () => {
    const stage = new ReportReadyStage()
    const { stub, runtimeReady } = makeSignalrStub()
    runtimeReady.mockRejectedValueOnce(new Error('hub down'))
    const result = await stage.run({
      config: makeConfig(),
      signalr: stub,
      logger: makeLogger() as unknown as import('pino').Logger,
      signal: new AbortController().signal,
    })
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toContain('hub down')
    }
  })
})
