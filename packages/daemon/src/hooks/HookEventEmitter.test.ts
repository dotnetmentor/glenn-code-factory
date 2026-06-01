// Tests for HookEventEmitter. The SignalR client is a fake — only `invoke` is
// stubbed; we never touch a real HubConnection. The HooksModule lifecycle
// events are constructed directly so we don't take an inadvertent dep on
// HooksModule's spec semantics.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { HookResult, HookSpec } from './HookExecutor.js'
import {
  DEFAULT_MAX_TAIL_BYTES,
  HookEventEmitter,
} from './HookEventEmitter.js'
import type { HookLifecycleEvent } from './HooksModule.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'

// ============================================================================
// Test helpers
// ============================================================================

const RUNTIME_ID = 'rt-123'

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

function buildEmitter(opts?: { maxTailBytes?: number }) {
  const invoke = vi.fn().mockResolvedValue(undefined)
  const signalr = { invoke } as unknown as SignalRClient
  const logger = makeLogger()
  const emitter = new HookEventEmitter({
    signalr,
    runtimeId: RUNTIME_ID,
    logger: logger as unknown as Logger,
    ...(opts?.maxTailBytes !== undefined ? { maxTailBytes: opts.maxTailBytes } : {}),
  })
  return { emitter, invoke, logger }
}

function spec(partial: Partial<HookSpec> & Pick<HookSpec, 'name'>): HookSpec {
  return {
    name: partial.name,
    cmd: partial.cmd ?? `echo ${partial.name}`,
    feedbackMode: partial.feedbackMode ?? 'on-failure',
  }
}

function makeResult(partial: Partial<HookResult> = {}): HookResult {
  return {
    exitCode: 'exitCode' in partial ? (partial.exitCode as number | null) : 0,
    durationMs: partial.durationMs ?? 42,
    outputTail: partial.outputTail ?? 'ok\n',
    outputHash: partial.outputHash ?? 'a'.repeat(64),
    timedOut: partial.timedOut ?? false,
    wasConfigError: partial.wasConfigError ?? false,
    onProgressLines: partial.onProgressLines ?? [],
  }
}

const ISO_FIXED = new Date('2026-05-08T12:00:00.000Z')

// ============================================================================
// Tests
// ============================================================================

describe('HookEventEmitter', () => {
  describe('lifecycle bridging', () => {
    it("invokes 'HookStarted' with all fields from a started event + ctx", () => {
      const { emitter, invoke } = buildEmitter()

      const event: HookLifecycleEvent = {
        type: 'started',
        spec: spec({ name: 'lint', cmd: 'npm run lint', feedbackMode: 'on-failure' }),
        point: 'beforePrompt',
        executionId: 'exec-1',
        startedAt: ISO_FIXED,
      }

      emitter.emitLifecycle(event, { conversationId: 'conv-1', turnId: 'turn-1' })

      expect(invoke).toHaveBeenCalledTimes(1)
      expect(invoke).toHaveBeenCalledWith('HookStarted', {
        executionId: 'exec-1',
        runtimeId: RUNTIME_ID,
        conversationId: 'conv-1',
        turnId: 'turn-1',
        hookPoint: 'BeforePrompt',
        hookName: 'lint',
        cmd: 'npm run lint',
        feedbackMode: 'OnFailure',
        startedAt: ISO_FIXED.toISOString(),
      })
    })

    it("started event without ctx leaves conversationId / turnId null", () => {
      const { emitter, invoke } = buildEmitter()
      emitter.emitLifecycle({
        type: 'started',
        spec: spec({ name: 'h', feedbackMode: 'always' }),
        point: 'afterPrompt',
        executionId: 'exec-2',
        startedAt: ISO_FIXED,
      })

      expect(invoke).toHaveBeenCalledTimes(1)
      const [, payload] = invoke.mock.calls[0]!
      expect(payload).toMatchObject({ conversationId: null, turnId: null })
    })

    it("invokes 'HookProgress' with stdoutLine + lineIndex", () => {
      const { emitter, invoke } = buildEmitter()
      emitter.emitLifecycle({
        type: 'progress',
        executionId: 'exec-3',
        line: 'building module foo',
        lineIndex: 7,
      })

      expect(invoke).toHaveBeenCalledTimes(1)
      expect(invoke).toHaveBeenCalledWith('HookProgress', {
        executionId: 'exec-3',
        runtimeId: RUNTIME_ID,
        stdoutLine: 'building module foo',
        lineIndex: 7,
      })
    })

    it("invokes 'HookCompleted' with endedAt as ISO string and tail unchanged when small", () => {
      const { emitter, invoke } = buildEmitter()
      const result = makeResult({
        exitCode: 0,
        durationMs: 1234,
        outputTail: 'success!\n',
        outputHash: 'b'.repeat(64),
        timedOut: false,
      })
      const endedAt = new Date('2026-05-08T12:00:42.500Z')

      emitter.emitLifecycle({
        type: 'completed',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-4',
        result,
        endedAt,
      })

      expect(invoke).toHaveBeenCalledTimes(1)
      expect(invoke).toHaveBeenCalledWith('HookCompleted', {
        executionId: 'exec-4',
        runtimeId: RUNTIME_ID,
        exitCode: 0,
        durationMs: 1234,
        outputTail: 'success!\n',
        outputHash: 'b'.repeat(64),
        timedOut: false,
        endedAt: endedAt.toISOString(),
      })
    })

    it("completed event with null exitCode (killed mid-run) surfaces as -1", () => {
      const { emitter, invoke } = buildEmitter()
      emitter.emitLifecycle({
        type: 'completed',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-5',
        result: makeResult({ exitCode: null, timedOut: false }),
        endedAt: ISO_FIXED,
      })
      const [, payload] = invoke.mock.calls[0]!
      expect(payload).toMatchObject({ exitCode: -1 })
    })

    it("invokes 'HookConfigError' with the reason copied from the event", () => {
      const { emitter, invoke } = buildEmitter()
      const endedAt = new Date('2026-05-08T12:01:00.000Z')

      emitter.emitLifecycle({
        type: 'configError',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-6',
        reason: 'hook command failed configuration check',
        outputTail: 'npm ERR! missing script: lint\n',
        endedAt,
      })

      expect(invoke).toHaveBeenCalledTimes(1)
      expect(invoke).toHaveBeenCalledWith('HookConfigError', {
        executionId: 'exec-6',
        runtimeId: RUNTIME_ID,
        reason: 'hook command failed configuration check',
        outputTail: 'npm ERR! missing script: lint\n',
        endedAt: endedAt.toISOString(),
      })
    })
  })

  describe('enum mapping', () => {
    it.each([
      ['beforePrompt', 'BeforePrompt'],
      ['afterPrompt', 'AfterPrompt'],
      ['onFileChange', 'OnFileChange'],
      ['beforeCommit', 'BeforeCommit'],
    ] as const)('maps HookPoint %s → %s', (point, wire) => {
      const { emitter, invoke } = buildEmitter()
      emitter.emitLifecycle({
        type: 'started',
        spec: spec({ name: 'h' }),
        point,
        executionId: `exec-${point}`,
        startedAt: ISO_FIXED,
      })
      const [, payload] = invoke.mock.calls[0]!
      expect((payload as { hookPoint: string }).hookPoint).toBe(wire)
    })

    it.each([
      ['on-failure', 'OnFailure'],
      ['always', 'Always'],
      ['silent', 'Silent'],
    ] as const)('maps HookFeedbackMode %s → %s', (mode, wire) => {
      const { emitter, invoke } = buildEmitter()
      emitter.emitLifecycle({
        type: 'started',
        spec: spec({ name: 'h', feedbackMode: mode }),
        point: 'beforePrompt',
        executionId: `exec-${mode}`,
        startedAt: ISO_FIXED,
      })
      const [, payload] = invoke.mock.calls[0]!
      expect((payload as { feedbackMode: string }).feedbackMode).toBe(wire)
    })
  })

  describe('output tail truncation', () => {
    it('passes a small tail (100 bytes) through unchanged', () => {
      const { emitter, invoke } = buildEmitter()
      const tail = 'x'.repeat(100)
      emitter.emitLifecycle({
        type: 'completed',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-small',
        result: makeResult({ outputTail: tail }),
        endedAt: ISO_FIXED,
      })
      const [, payload] = invoke.mock.calls[0]!
      const out = (payload as { outputTail: string }).outputTail
      expect(out).toBe(tail)
      expect(out).not.toContain('[truncated')
    })

    it('clamps a 32 KiB tail to ≤ 16 KiB with a \\n[truncated N bytes]\\n marker', () => {
      const { emitter, invoke } = buildEmitter()
      const sourceBytes = 32 * 1024
      const tail = 'A'.repeat(sourceBytes)

      emitter.emitLifecycle({
        type: 'completed',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-big',
        result: makeResult({ outputTail: tail }),
        endedAt: ISO_FIXED,
      })

      const [, payload] = invoke.mock.calls[0]!
      const out = (payload as { outputTail: string }).outputTail
      const outBytes = Buffer.byteLength(out, 'utf8')

      expect(outBytes).toBeLessThanOrEqual(DEFAULT_MAX_TAIL_BYTES)
      // Marker shape and accuracy of N.
      const m = out.match(/\n\[truncated (\d+) bytes\]\n$/)
      expect(m).not.toBeNull()
      const N = Number(m![1])
      const keptBytes = outBytes - Buffer.byteLength(`\n[truncated ${N} bytes]\n`, 'utf8')
      expect(N).toBe(sourceBytes - keptBytes)
    })

    it('respects a custom maxTailBytes override', () => {
      const { emitter, invoke } = buildEmitter({ maxTailBytes: 256 })
      const tail = 'B'.repeat(2048)
      emitter.emitLifecycle({
        type: 'configError',
        spec: spec({ name: 'h' }),
        point: 'beforePrompt',
        executionId: 'exec-cap',
        reason: 'r',
        outputTail: tail,
        endedAt: ISO_FIXED,
      })
      const [, payload] = invoke.mock.calls[0]!
      const out = (payload as { outputTail: string }).outputTail
      expect(Buffer.byteLength(out, 'utf8')).toBeLessThanOrEqual(256)
      expect(out).toMatch(/\n\[truncated \d+ bytes\]\n$/)
    })
  })

  describe('self-heal', () => {
    it("emitSelfHealStarted invokes 'HookSelfHealStarted' with the payload as-is", () => {
      const { emitter, invoke } = buildEmitter()
      const payload = {
        runtimeId: RUNTIME_ID,
        conversationId: 'conv-1',
        previousTurnId: 'turn-old',
        newTurnId: 'turn-new',
        iteration: 2,
      }
      emitter.emitSelfHealStarted(payload)
      expect(invoke).toHaveBeenCalledWith('HookSelfHealStarted', payload)
    })

    it("emitSelfHealMaxedOut invokes 'HookSelfHealMaxedOut' with the payload as-is", () => {
      const { emitter, invoke } = buildEmitter()
      const payload = {
        runtimeId: RUNTIME_ID,
        conversationId: 'conv-1',
        turnId: 'turn-x',
        iteration: 3,
      }
      emitter.emitSelfHealMaxedOut(payload)
      expect(invoke).toHaveBeenCalledWith('HookSelfHealMaxedOut', payload)
    })
  })

  describe('error handling', () => {
    it('swallows a rejected invoke and logs warn with the method name', async () => {
      const invoke = vi.fn().mockRejectedValueOnce(new Error('disconnected'))
      const signalr = { invoke } as unknown as SignalRClient
      const logger = makeLogger()
      const emitter = new HookEventEmitter({
        signalr,
        runtimeId: RUNTIME_ID,
        logger: logger as unknown as Logger,
      })

      // Must not throw synchronously.
      expect(() =>
        emitter.emitLifecycle({
          type: 'started',
          spec: spec({ name: 'h' }),
          point: 'beforePrompt',
          executionId: 'exec-fail',
          startedAt: ISO_FIXED,
        }),
      ).not.toThrow()

      // Drain microtasks so the rejection lands on .catch().
      await vi.waitFor(() => {
        expect(logger.warn).toHaveBeenCalledTimes(1)
      })

      const [logArgs] = logger.warn.mock.calls[0]!
      expect(logArgs).toMatchObject({
        method: 'HookStarted',
        executionId: 'exec-fail',
      })
      // Error object is on the log obj as `err`.
      expect((logArgs as { err: Error }).err).toBeInstanceOf(Error)
    })

    it('emitLifecycle returns void synchronously even when invoke is pending', () => {
      // Build an emitter whose invoke returns a never-resolving promise. The
      // call must still complete synchronously.
      let _settle: (() => void) | undefined
      const pending = new Promise<void>((resolve) => {
        _settle = resolve
      })
      const invoke = vi.fn().mockReturnValue(pending)
      const signalr = { invoke } as unknown as SignalRClient
      const logger = makeLogger()
      const emitter = new HookEventEmitter({
        signalr,
        runtimeId: RUNTIME_ID,
        logger: logger as unknown as Logger,
      })

      const ret = emitter.emitLifecycle({
        type: 'progress',
        executionId: 'exec-p',
        line: 'hello',
        lineIndex: 0,
      })

      expect(ret).toBeUndefined()
      expect(invoke).toHaveBeenCalledTimes(1)
      // Hand control back to the runtime so the dangling promise is GC'd cleanly.
      _settle?.()
    })
  })
})
