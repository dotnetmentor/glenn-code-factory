// Tests for HooksModule. The executor is a stub — this card is about
// dispatch, ordering, event shape, and feedback-mode semantics, not real
// shell execution (that's covered in HookExecutor.test.ts).

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import {
  HooksModule,
  type HookConfig,
  type HookLifecycleEvent,
  type HookRunCtx,
} from './HooksModule.js'
import type { HookExecutor, HookResult, HookSpec } from './HookExecutor.js'

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

function buildModule(executorBehaviour: (spec: HookSpec) => Promise<HookResult>) {
  const runFn = vi.fn(executorBehaviour)
  const executor = { run: runFn } as unknown as HookExecutor
  const logger = makeLogger() as unknown as Logger
  const module = new HooksModule({
    executor,
    logger,
    generateExecutionId: () => 'fixed-id',
  })
  return { module, executor, runFn, logger }
}

function spec(partial: Partial<HookSpec> & Pick<HookSpec, 'name'>): HookSpec {
  return {
    name: partial.name,
    cmd: partial.cmd ?? `echo ${partial.name}`,
    feedbackMode: partial.feedbackMode ?? 'on-failure',
    ...(partial.timeoutMs !== undefined ? { timeoutMs: partial.timeoutMs } : {}),
  }
}

function makeResult(partial: Partial<HookResult> = {}): HookResult {
  // NOTE: use `'exitCode' in partial` rather than `??` because the caller may
  // legitimately pass `exitCode: null` (the killed-mid-run case) and `??`
  // would silently coerce it back to 0.
  return {
    exitCode: 'exitCode' in partial ? (partial.exitCode as number | null) : 0,
    durationMs: partial.durationMs ?? 10,
    outputTail: partial.outputTail ?? '',
    outputHash: partial.outputHash ?? 'a'.repeat(64),
    timedOut: partial.timedOut ?? false,
    wasConfigError: partial.wasConfigError ?? false,
    onProgressLines: partial.onProgressLines ?? [],
  }
}

function buildCtx(overrides: Partial<HookRunCtx> = {}): {
  ctx: HookRunCtx
  events: HookLifecycleEvent[]
  controller: AbortController
} {
  const events: HookLifecycleEvent[] = []
  const controller = overrides.signal ? null : new AbortController()
  const ctx: HookRunCtx = {
    point: overrides.point ?? 'beforePrompt',
    signal: overrides.signal ?? controller!.signal,
    onEvent: overrides.onEvent ?? ((e) => events.push(e)),
    ...(overrides.conversationId !== undefined ? { conversationId: overrides.conversationId } : {}),
    ...(overrides.turnId !== undefined ? { turnId: overrides.turnId } : {}),
  }
  return { ctx, events, controller: controller! }
}

function emptyConfig(): HookConfig {
  return {
    beforePrompt: [],
    afterPrompt: [],
    onFileChange: [],
    beforeCommit: [],
  }
}

// ============================================================================
// Tests
// ============================================================================

describe('HooksModule', () => {
  it('returns ranAll=true and emits no events when config is empty', async () => {
    const { module, runFn } = buildModule(async () => makeResult())
    module.setConfig(emptyConfig())
    const { ctx, events } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result).toEqual({ ranAll: true, failures: [], feedbackTexts: [] })
    expect(runFn).not.toHaveBeenCalled()
    expect(events).toEqual([])
  })

  it('runs all specs sequentially on success in on-failure mode', async () => {
    const { module, runFn } = buildModule(async () => makeResult({ exitCode: 0 }))
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'one', feedbackMode: 'on-failure' }),
        spec({ name: 'two', feedbackMode: 'on-failure' }),
      ],
    })
    const { ctx, events } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(true)
    expect(result.failures).toEqual([])
    expect(result.feedbackTexts).toEqual([])
    expect(runFn).toHaveBeenCalledTimes(2)

    const types = events.map((e) => e.type)
    expect(types).toEqual(['started', 'completed', 'started', 'completed'])
  })

  it('stops on first failure in on-failure mode and emits feedback text from outputTail', async () => {
    let call = 0
    const { module, runFn } = buildModule(async () => {
      call++
      if (call === 1) {
        return makeResult({ exitCode: 1, outputTail: 'something broke' })
      }
      return makeResult({ exitCode: 0 })
    })
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'one', feedbackMode: 'on-failure' }),
        spec({ name: 'two', feedbackMode: 'on-failure' }),
      ],
    })
    const { ctx } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(false)
    expect(result.failures).toHaveLength(1)
    expect(result.failures[0]?.spec.name).toBe('one')
    expect(result.feedbackTexts).toEqual(['something broke'])
    expect(runFn).toHaveBeenCalledTimes(1)
  })

  it('produces a "succeeded" feedback text in always mode on success', async () => {
    const { module } = buildModule(async () =>
      makeResult({ exitCode: 0, outputTail: 'all good' }),
    )
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'check', feedbackMode: 'always' })],
    })
    const { ctx } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(true)
    expect(result.feedbackTexts).toHaveLength(1)
    expect(result.feedbackTexts[0]).toContain('succeeded')
    expect(result.feedbackTexts[0]).toContain('all good')
    expect(result.feedbackTexts[0]).toContain('check')
  })

  it('produces a "failed (exit N)" feedback text in always mode on failure and continues the loop', async () => {
    let call = 0
    const { module, runFn } = buildModule(async () => {
      call++
      if (call === 1) {
        return makeResult({ exitCode: 1, outputTail: 'tail-of-failure' })
      }
      return makeResult({ exitCode: 0, outputTail: 'tail-of-success' })
    })
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'first', feedbackMode: 'always' }),
        spec({ name: 'second', feedbackMode: 'always' }),
      ],
    })
    const { ctx } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(true)
    expect(runFn).toHaveBeenCalledTimes(2)
    expect(result.feedbackTexts).toHaveLength(2)
    expect(result.feedbackTexts[0]).toContain('failed (exit 1)')
    expect(result.feedbackTexts[0]).toContain('tail-of-failure')
    expect(result.feedbackTexts[1]).toContain('succeeded')
  })

  it('silent mode produces no feedback, no failure tracking, and does not stop the loop on failure', async () => {
    let call = 0
    const { module, runFn } = buildModule(async () => {
      call++
      if (call === 1) {
        return makeResult({ exitCode: 1, outputTail: 'noisy' })
      }
      return makeResult({ exitCode: 0 })
    })
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'a', feedbackMode: 'silent' }),
        spec({ name: 'b', feedbackMode: 'silent' }),
      ],
    })
    const { ctx } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(true)
    expect(result.failures).toEqual([])
    expect(result.feedbackTexts).toEqual([])
    expect(runFn).toHaveBeenCalledTimes(2)
  })

  it('emits configError instead of completed and short-circuits the loop', async () => {
    let call = 0
    const { module, runFn } = buildModule(async () => {
      call++
      if (call === 1) {
        return makeResult({
          exitCode: 127,
          outputTail: 'command not found',
          wasConfigError: true,
        })
      }
      return makeResult({ exitCode: 0 })
    })
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'broken', feedbackMode: 'on-failure' }),
        spec({ name: 'should-not-run', feedbackMode: 'on-failure' }),
      ],
    })
    const { ctx, events } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(false)
    expect(result.failures).toEqual([])
    expect(result.feedbackTexts).toEqual([])
    expect(runFn).toHaveBeenCalledTimes(1)

    const types = events.map((e) => e.type)
    expect(types).toEqual(['started', 'configError'])
    expect(types).not.toContain('completed')

    const cfgErr = events.find((e) => e.type === 'configError')!
    expect(cfgErr.type).toBe('configError')
    if (cfgErr.type === 'configError') {
      expect(cfgErr.spec.name).toBe('broken')
      expect(cfgErr.outputTail).toBe('command not found')
      expect(cfgErr.point).toBe('beforePrompt')
    }
  })

  it('hot-swaps config between runs without affecting an in-flight run', async () => {
    let resolveFirst: ((v: HookResult) => void) | null = null
    let executorCalls: string[] = []

    const { module } = buildModule(async (s) => {
      executorCalls.push(s.name)
      // First spec on first run blocks until we let it through, simulating
      // an in-flight run while we mutate config.
      if (s.name === 'old-1') {
        return new Promise<HookResult>((resolve) => {
          resolveFirst = resolve
        })
      }
      return makeResult({ exitCode: 0 })
    })

    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'old-1', feedbackMode: 'on-failure' }),
        spec({ name: 'old-2', feedbackMode: 'on-failure' }),
      ],
    })

    const { ctx: ctxA } = buildCtx()
    const firstRun = module.run('beforePrompt', ctxA)

    // Wait for the executor to actually start the first spec.
    await new Promise((r) => setTimeout(r, 0))
    expect(executorCalls).toEqual(['old-1'])

    // Hot-swap mid-flight. The first run must NOT pick this up.
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'new-1', feedbackMode: 'on-failure' })],
    })

    // Let the in-flight executor call complete.
    resolveFirst!(makeResult({ exitCode: 0 }))
    const firstResult = await firstRun

    expect(firstResult.ranAll).toBe(true)
    // First run still iterated the OLD array.
    expect(executorCalls).toEqual(['old-1', 'old-2'])

    // Second run picks up the NEW array.
    executorCalls = []
    const { ctx: ctxB } = buildCtx()
    const secondResult = await module.run('beforePrompt', ctxB)
    expect(secondResult.ranAll).toBe(true)
    expect(executorCalls).toEqual(['new-1'])
  })

  it('kill switch returns immediately without calling the executor', async () => {
    const { module, runFn } = buildModule(async () => makeResult({ exitCode: 0 }))
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'a' }), spec({ name: 'b' })],
    })
    module.setKillSwitch(true)
    const { ctx, events } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result).toEqual({ ranAll: true, failures: [], feedbackTexts: [] })
    expect(runFn).not.toHaveBeenCalled()
    expect(events).toEqual([])
  })

  it('returns immediately without calling the executor when signal is already aborted', async () => {
    const { module, runFn } = buildModule(async () => makeResult({ exitCode: 0 }))
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'a' })],
    })

    const controller = new AbortController()
    controller.abort()
    const events: HookLifecycleEvent[] = []
    const ctx: HookRunCtx = {
      point: 'beforePrompt',
      signal: controller.signal,
      onEvent: (e) => events.push(e),
    }

    const result = await module.run('beforePrompt', ctx)

    expect(result).toEqual({ ranAll: false, failures: [], feedbackTexts: [] })
    expect(runFn).not.toHaveBeenCalled()
    expect(events).toEqual([])
  })

  it('treats aborted-mid-run (exitCode null, timedOut false) as a failure in on-failure mode', async () => {
    const { module } = buildModule(async () =>
      makeResult({ exitCode: null, timedOut: false, outputTail: 'killed mid-run' }),
    )
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [
        spec({ name: 'a', feedbackMode: 'on-failure' }),
        spec({ name: 'b', feedbackMode: 'on-failure' }),
      ],
    })
    const { ctx } = buildCtx()

    const result = await module.run('beforePrompt', ctx)

    expect(result.ranAll).toBe(false)
    expect(result.failures).toHaveLength(1)
    expect(result.feedbackTexts).toEqual(['killed mid-run'])
  })

  it('emits progress events post-hoc between started and completed', async () => {
    const { module } = buildModule(async () =>
      makeResult({ exitCode: 0, onProgressLines: ['line1', 'line2'] }),
    )
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'noisy', feedbackMode: 'on-failure' })],
    })
    const { ctx, events } = buildCtx()

    await module.run('beforePrompt', ctx)

    const types = events.map((e) => e.type)
    expect(types).toEqual(['started', 'progress', 'progress', 'completed'])

    const progressA = events[1]
    const progressB = events[2]
    if (progressA?.type === 'progress' && progressB?.type === 'progress') {
      expect(progressA.line).toBe('line1')
      expect(progressA.lineIndex).toBe(0)
      expect(progressA.executionId).toBe('fixed-id')
      expect(progressB.line).toBe('line2')
      expect(progressB.lineIndex).toBe(1)
    } else {
      throw new Error('expected progress events at indices 1 and 2')
    }
  })

  it('silent mode suppresses progress events but still emits started + completed', async () => {
    const { module } = buildModule(async () =>
      makeResult({ exitCode: 0, onProgressLines: ['line1', 'line2'] }),
    )
    module.setConfig({
      ...emptyConfig(),
      beforePrompt: [spec({ name: 'noisy', feedbackMode: 'silent' })],
    })
    const { ctx, events } = buildCtx()

    await module.run('beforePrompt', ctx)

    const types = events.map((e) => e.type)
    expect(types).toEqual(['started', 'completed'])
    expect(types).not.toContain('progress')
  })

  it('started/completed events carry spec, point, executionId, and timestamps', async () => {
    const result = makeResult({ exitCode: 0, outputTail: 'done' })
    const { module } = buildModule(async () => result)
    const theSpec = spec({ name: 'shape', feedbackMode: 'on-failure' })
    module.setConfig({ ...emptyConfig(), afterPrompt: [theSpec] })
    const { ctx, events } = buildCtx({ point: 'afterPrompt' })

    await module.run('afterPrompt', ctx)

    expect(events).toHaveLength(2)
    const started = events[0]
    const completed = events[1]

    expect(started?.type).toBe('started')
    if (started?.type === 'started') {
      expect(started.spec).toBe(theSpec)
      expect(started.point).toBe('afterPrompt')
      expect(started.executionId).toBe('fixed-id')
      expect(started.startedAt).toBeInstanceOf(Date)
    }

    expect(completed?.type).toBe('completed')
    if (completed?.type === 'completed') {
      expect(completed.spec).toBe(theSpec)
      expect(completed.point).toBe('afterPrompt')
      expect(completed.executionId).toBe('fixed-id')
      expect(completed.endedAt).toBeInstanceOf(Date)
      expect(completed.result).toBe(result)
    }
  })
})
