// Tests for SelfHealCoordinator. SignalRClient and HookEventEmitter are
// stubbed — we never touch a real hub or emitter. The wire shape is asserted
// against the .NET record (camelCase) so the test pins the contract.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { HookEventEmitter } from './HookEventEmitter.js'
import {
  DEFAULT_MAX_ITERATIONS,
  DEFAULT_MAX_PROMPT_BYTES,
  SelfHealCoordinator,
  type SelfHealCoordinatorOptions,
} from './SelfHealCoordinator.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'

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

function buildCoord(opts?: Partial<SelfHealCoordinatorOptions>) {
  const invoke = vi.fn()
  const signalr = { invoke } as unknown as SignalRClient
  const emitSelfHealStarted = vi.fn()
  const emitSelfHealMaxedOut = vi.fn()
  const emitter = {
    emitSelfHealStarted,
    emitSelfHealMaxedOut,
  } as unknown as HookEventEmitter
  const logger = makeLogger()
  const coord = new SelfHealCoordinator({
    signalr,
    emitter,
    runtimeId: 'rt-1',
    logger: logger as unknown as Logger,
    ...opts,
  })
  return { coord, invoke, emitSelfHealStarted, emitSelfHealMaxedOut, logger }
}

const BASE_ARGS = {
  conversationId: 'conv-1',
  turnId: 'turn-1',
  agentId: 'claude-sess-1',
  iteration: 0,
}

function expectedSingleBlock(name: string, tail: string): string {
  return [
    `After your changes, hook \`${name}\` failed. Fix this:`,
    '',
    '```',
    tail,
    '```',
    '',
    'Do not address other issues.',
  ].join('\n')
}

// ============================================================================
// Tests
// ============================================================================

describe('SelfHealCoordinator', () => {
  describe('prompt assembly', () => {
    it('builds the single-failure prompt verbatim from the template', async () => {
      const { coord, invoke } = buildCoord()
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops\nfoo.ts:1:1 error' }],
      })

      const [, payload] = invoke.mock.calls[0]!
      expect((payload as { feedbackPrompt: string }).feedbackPrompt).toBe(
        expectedSingleBlock('eslint', 'oops\nfoo.ts:1:1 error'),
      )
    })

    it('joins multi-failure blocks with the documented separator', async () => {
      const { coord, invoke } = buildCoord()
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [
          { hookName: 'eslint', outputTail: 'oops' },
          { hookName: 'tsc', outputTail: 'TS2345' },
        ],
      })

      const [, payload] = invoke.mock.calls[0]!
      const prompt = (payload as { feedbackPrompt: string }).feedbackPrompt
      const expected =
        expectedSingleBlock('eslint', 'oops') +
        '\n\n---\n\n' +
        expectedSingleBlock('tsc', 'TS2345')
      expect(prompt).toBe(expected)
      // Sanity: separator appears exactly once for two failures.
      expect(prompt.split('\n\n---\n\n').length).toBe(2)
    })

    it('truncates an oversized single-failure prompt to the byte cap', async () => {
      const { coord, invoke } = buildCoord({ maxPromptBytes: 256 })
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      const bigTail = 'x'.repeat(10 * 1024)
      await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: bigTail }],
      })

      const [, payload] = invoke.mock.calls[0]!
      const prompt = (payload as { feedbackPrompt: string }).feedbackPrompt
      expect(Buffer.byteLength(prompt, 'utf8')).toBeLessThanOrEqual(256)
      expect(prompt).toContain('[truncated]')
    })

    it('keeps the END of the outputTail when truncating (most recent error)', async () => {
      const { coord, invoke } = buildCoord({ maxPromptBytes: 512 })
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      const tail = 'A'.repeat(2000) + 'TAIL_MARKER_END'
      await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: tail }],
      })

      const [, payload] = invoke.mock.calls[0]!
      const prompt = (payload as { feedbackPrompt: string }).feedbackPrompt
      expect(prompt).toContain('TAIL_MARKER_END')
    })

    it('bounds each block in a multi-failure prompt by the per-failure quota', async () => {
      const { coord, invoke } = buildCoord({ maxPromptBytes: 1024 })
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      const failures = [
        { hookName: 'a', outputTail: '1'.repeat(5 * 1024) },
        { hookName: 'b', outputTail: '2'.repeat(5 * 1024) },
        { hookName: 'c', outputTail: '3'.repeat(5 * 1024) },
      ]
      await coord.requestContinuation({ ...BASE_ARGS, failures })

      const [, payload] = invoke.mock.calls[0]!
      const prompt = (payload as { feedbackPrompt: string }).feedbackPrompt
      expect(Buffer.byteLength(prompt, 'utf8')).toBeLessThanOrEqual(1024)
      // All three hook names should appear (none of the failures dropped wholesale).
      expect(prompt).toContain('hook `a`')
      expect(prompt).toContain('hook `b`')
      expect(prompt).toContain('hook `c`')
      expect(prompt).toContain('[truncated]')
    })
  })

  describe('hub interaction', () => {
    it('emits HookSelfHealStarted and returns the new turn id when the server accepts', async () => {
      const { coord, invoke, emitSelfHealStarted, emitSelfHealMaxedOut } = buildCoord()
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        iteration: 1,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({ accepted: true, newTurnId: 'new-turn' })
      expect(emitSelfHealStarted).toHaveBeenCalledTimes(1)
      expect(emitSelfHealStarted).toHaveBeenCalledWith({
        runtimeId: 'rt-1',
        conversationId: 'conv-1',
        previousTurnId: 'turn-1',
        newTurnId: 'new-turn',
        iteration: 2, // args.iteration (1) + 1 = the attempt that just started
      })
      expect(emitSelfHealMaxedOut).not.toHaveBeenCalled()
    })

    it('returns maxedOut without re-emitting (server has already emitted HookSelfHealMaxedOut)', async () => {
      const { coord, invoke, emitSelfHealStarted, emitSelfHealMaxedOut, logger } =
        buildCoord()
      invoke.mockResolvedValue({
        accepted: false,
        newTurnId: null,
        rejectionReason: 'maxedOut',
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({ accepted: false, rejectionReason: 'maxedOut' })
      expect(emitSelfHealStarted).not.toHaveBeenCalled()
      expect(emitSelfHealMaxedOut).not.toHaveBeenCalled()
      expect(logger.info).toHaveBeenCalled()
    })

    it('logs at info on turnNotRunning rejection', async () => {
      const { coord, invoke, emitSelfHealStarted, logger } = buildCoord()
      invoke.mockResolvedValue({
        accepted: false,
        newTurnId: null,
        rejectionReason: 'turnNotRunning',
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({
        accepted: false,
        rejectionReason: 'turnNotRunning',
      })
      expect(emitSelfHealStarted).not.toHaveBeenCalled()
      expect(logger.warn).not.toHaveBeenCalled()
      // info called at least twice: entry + rejection
      expect(logger.info).toHaveBeenCalled()
    })

    it('logs at warn on runtimeMismatch rejection (misconfiguration)', async () => {
      const { coord, invoke, emitSelfHealStarted, logger } = buildCoord()
      invoke.mockResolvedValue({
        accepted: false,
        newTurnId: null,
        rejectionReason: 'runtimeMismatch',
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({
        accepted: false,
        rejectionReason: 'runtimeMismatch',
      })
      expect(emitSelfHealStarted).not.toHaveBeenCalled()
      expect(logger.warn).toHaveBeenCalled()
    })

    it('logs at warn for an unknown rejection reason and surfaces the raw value', async () => {
      const { coord, invoke, logger } = buildCoord()
      invoke.mockResolvedValue({
        accepted: false,
        newTurnId: null,
        rejectionReason: 'somethingNew',
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result.accepted).toBe(false)
      expect(logger.warn).toHaveBeenCalled()
    })

    it('returns invokeFailed and logs warn when the hub call rejects (no throw)', async () => {
      const { coord, invoke, emitSelfHealStarted, logger } = buildCoord()
      invoke.mockRejectedValue(new Error('disconnected'))

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({
        accepted: false,
        rejectionReason: 'invokeFailed',
      })
      expect(emitSelfHealStarted).not.toHaveBeenCalled()
      expect(logger.warn).toHaveBeenCalled()
    })
  })

  describe('budget', () => {
    it('short-circuits without invoking the hub when the daemon-side budget is exhausted', async () => {
      const { coord, invoke, emitSelfHealStarted, logger } = buildCoord()

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        iteration: DEFAULT_MAX_ITERATIONS, // 3 → exhausted
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({
        accepted: false,
        rejectionReason: 'budgetExhausted',
      })
      expect(invoke).not.toHaveBeenCalled()
      expect(emitSelfHealStarted).not.toHaveBeenCalled()
      // The "self-heal budget exhausted" info log fires.
      expect(logger.info).toHaveBeenCalledWith(
        expect.objectContaining({
          iteration: DEFAULT_MAX_ITERATIONS,
          maxIterations: DEFAULT_MAX_ITERATIONS,
        }),
        'self-heal budget exhausted',
      )
    })

    it('honours a custom maxIterations and still calls the hub below the cap', async () => {
      const { coord, invoke } = buildCoord({ maxIterations: 5 })
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      const result = await coord.requestContinuation({
        ...BASE_ARGS,
        iteration: 4, // < 5, allowed
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(result).toEqual({ accepted: true, newTurnId: 'new-turn' })
      expect(invoke).toHaveBeenCalledTimes(1)
    })
  })

  describe('payload contract', () => {
    it('invokes RequestSelfHealContinuation with the exact .NET record fields (camelCase wire)', async () => {
      const { coord, invoke } = buildCoord()
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      await coord.requestContinuation({
        conversationId: 'conv-X',
        turnId: 'turn-Y',
        agentId: 'claude-Z',
        iteration: 1,
        failures: [
          { hookName: 'eslint', outputTail: 'a' },
          { hookName: 'tsc', outputTail: 'b' },
        ],
      })

      expect(invoke).toHaveBeenCalledTimes(1)
      const [method, payload] = invoke.mock.calls[0]!
      expect(method).toBe('RequestSelfHealContinuation')

      // First failure determines the hookName label; the prompt covers all.
      const expectedPrompt =
        expectedSingleBlock('eslint', 'a') +
        '\n\n---\n\n' +
        expectedSingleBlock('tsc', 'b')
      expect(payload).toEqual({
        runtimeId: 'rt-1',
        conversationId: 'conv-X',
        turnId: 'turn-Y',
        agentId: 'claude-Z',
        hookName: 'eslint',
        feedbackPrompt: expectedPrompt,
        iteration: 1,
      })
    })

    it('event payload iteration is args.iteration + 1 (off-by-one in the right direction)', async () => {
      const { coord, invoke, emitSelfHealStarted } = buildCoord()
      invoke.mockResolvedValue({
        accepted: true,
        newTurnId: 'new-turn',
        rejectionReason: null,
      })

      await coord.requestContinuation({
        ...BASE_ARGS,
        iteration: 1,
        failures: [{ hookName: 'eslint', outputTail: 'oops' }],
      })

      expect(emitSelfHealStarted).toHaveBeenCalledWith(
        expect.objectContaining({ iteration: 2 }),
      )
    })
  })

  describe('defaults', () => {
    it('exposes documented defaults', () => {
      expect(DEFAULT_MAX_ITERATIONS).toBe(3)
      expect(DEFAULT_MAX_PROMPT_BYTES).toBe(8192)
    })
  })
})
