// Tests for DestructiveOpGate. Hand-rolled fakes for GitModule + SignalRClient
// (vs. spinning up the real ones with a fake GitRunner) — the gate's contract
// is narrow enough that the smaller fakes keep the suite fast and the
// assertions sharp. Vitest fake timers drive the 5min expiry path.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { DestructiveOpGate, type DestructiveOpGitModule } from './DestructiveOpGate.js'
import type { GitInvocation } from './types.js'

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

function makeGitModuleStub() {
  const runRaw = vi.fn(async (_inv: GitInvocation) => ({
    ok: true,
    outputTail: '',
    exitCode: 0 as number | null,
  }))
  const merge = vi.fn(async (_branch: string) => ({ ok: true }))
  const stub: DestructiveOpGitModule = { runRaw, merge }
  return { stub, runRaw, merge }
}

function makeSignalrStub() {
  const invoke = vi.fn(async (_method: string, ..._args: unknown[]) => ({
    approvalId: 'approval-1',
  }))
  const emitEvent = vi.fn(async (_p: EmitEventPayload) => {})
  const stub = { invoke, emitEvent } as unknown as Pick<SignalRClient, 'invoke' | 'emitEvent'>
  return { stub, invoke, emitEvent }
}

interface BuildOpts {
  expiryMs?: number
}

function build(opts: BuildOpts = {}) {
  const log = makeLogger()
  const git = makeGitModuleStub()
  const signalr = makeSignalrStub()
  const buildArgs: ConstructorParameters<typeof DestructiveOpGate>[0] = {
    gitModule: git.stub,
    signalr: signalr.stub,
    logger: log as unknown as Logger,
  }
  if (opts.expiryMs !== undefined) buildArgs.expiryMs = opts.expiryMs
  const gate = new DestructiveOpGate(buildArgs)
  return {
    gate,
    log,
    runRaw: git.runRaw,
    merge: git.merge,
    invoke: signalr.invoke,
    emitEvent: signalr.emitEvent,
  }
}

function inv(op: GitInvocation['op'], args: string[]): GitInvocation {
  return { op, args }
}

// ============================================================================
// Tests
// ============================================================================

describe('DestructiveOpGate', () => {
  describe('isDestructive', () => {
    it('flags op:Reset as destructive', () => {
      expect(DestructiveOpGate.isDestructive(inv('Reset', ['reset', '--soft', 'HEAD~1']))).toBe(
        true,
      )
    })

    it('flags op:ForcePush as destructive', () => {
      expect(DestructiveOpGate.isDestructive(inv('ForcePush', ['push', 'origin', 'main']))).toBe(
        true,
      )
    })

    it('flags op:BranchDelete as destructive', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchDelete', ['branch', '-d', 'feat']))).toBe(
        true,
      )
    })

    it('flags args matching `reset --hard`', () => {
      // op label says BranchList, args reveal the truth — gate still flags it.
      expect(
        DestructiveOpGate.isDestructive(inv('BranchList', ['reset', '--hard', 'HEAD~1'])),
      ).toBe(true)
    })

    it('flags args matching `reset --keep`', () => {
      expect(
        DestructiveOpGate.isDestructive(inv('BranchList', ['reset', '--keep', 'HEAD~1'])),
      ).toBe(true)
    })

    it('flags args matching `push --force`', () => {
      expect(
        DestructiveOpGate.isDestructive(inv('Push', ['push', '--force', 'origin', 'main'])),
      ).toBe(true)
    })

    it('flags args matching `push -f`', () => {
      expect(DestructiveOpGate.isDestructive(inv('Push', ['push', '-f', 'origin', 'main']))).toBe(
        true,
      )
    })

    it('flags args matching `push --force-with-lease`', () => {
      expect(
        DestructiveOpGate.isDestructive(
          inv('Push', ['push', '--force-with-lease', 'origin', 'main']),
        ),
      ).toBe(true)
    })

    it('flags args matching `branch -D`', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchList', ['branch', '-D', 'feat']))).toBe(
        true,
      )
    })

    it('flags args matching `branch --delete --force`', () => {
      expect(
        DestructiveOpGate.isDestructive(
          inv('BranchList', ['branch', '--delete', '--force', 'feat']),
        ),
      ).toBe(true)
    })

    it('flags args matching `clean -fd`', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchList', ['clean', '-fd']))).toBe(true)
    })

    it('flags args matching `clean -f -d`', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchList', ['clean', '-f', '-d']))).toBe(true)
    })

    it('flags args matching `checkout --force`', () => {
      expect(
        DestructiveOpGate.isDestructive(inv('Checkout', ['checkout', '--force', 'main'])),
      ).toBe(true)
    })

    it('does NOT flag a normal `push origin main`', () => {
      expect(DestructiveOpGate.isDestructive(inv('Push', ['push', 'origin', 'main']))).toBe(false)
    })

    it('does NOT flag `commit -m`', () => {
      expect(DestructiveOpGate.isDestructive(inv('Commit', ['commit', '-m', 'wip']))).toBe(false)
    })

    it('does NOT flag `branch --list`', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchList', ['branch', '--list']))).toBe(false)
    })

    it('does NOT flag `clean -n` (dry-run)', () => {
      expect(DestructiveOpGate.isDestructive(inv('BranchList', ['clean', '-n']))).toBe(false)
    })

    it('does NOT flag `checkout main` (no --force)', () => {
      expect(DestructiveOpGate.isDestructive(inv('Checkout', ['checkout', 'main']))).toBe(false)
    })
  })

  describe('requestApproval', () => {
    it('invokes RequestDestructiveGitOp with the expected payload + parks pending', async () => {
      const { gate, invoke, runRaw } = build()

      // Fire the request — don't await yet; the promise parks until execute.
      const promise = gate.requestApproval(
        inv('Reset', ['reset', '--hard', 'HEAD~1']),
        'rolling back failed deploy',
        { conversationId: 'c1', turnId: 't1' },
      )
      // Let the invoke microtask flush.
      await new Promise((r) => setImmediate(r))

      expect(invoke).toHaveBeenCalledTimes(1)
      expect(invoke).toHaveBeenCalledWith('RequestDestructiveGitOp', {
        opType: 'Reset',
        args: 'reset --hard HEAD~1',
        reason: 'rolling back failed deploy',
      })

      // Server says ok → gate parks the promise. runRaw must NOT have been
      // called yet (that only happens on handleExecuteApproved).
      expect(runRaw).not.toHaveBeenCalled()

      // Approve → gate executes via runRaw and resolves the parked promise.
      await gate.handleExecuteApproved('approval-1')
      const result = await promise
      expect(result).toEqual({ ok: true, outputTail: '' })
      expect(runRaw).toHaveBeenCalledTimes(1)
      expect(runRaw).toHaveBeenCalledWith(
        { op: 'Reset', args: ['reset', '--hard', 'HEAD~1'] },
        { conversationId: 'c1', turnId: 't1' },
      )
    })

    it('resolves with ok:false when signalr.invoke rejects (no pending entry retained)', async () => {
      const { gate, invoke, runRaw, log } = build()
      invoke.mockRejectedValueOnce(new Error('hub down'))

      const result = await gate.requestApproval(
        inv('ForcePush', ['push', '--force', 'origin', 'main']),
        'rebase landed',
      )

      expect(result.ok).toBe(false)
      expect(result.outputTail).toContain('hub down')
      expect(log.warn).toHaveBeenCalled()

      // No pending entry → a stray ExecuteDestructiveGitOp must no-op.
      await gate.handleExecuteApproved('approval-1')
      expect(runRaw).not.toHaveBeenCalled()
    })

    it('passes ctx through to runRaw on approval', async () => {
      const { gate, runRaw } = build()
      const promise = gate.requestApproval(
        inv('BranchDelete', ['branch', '-D', 'feat']),
        'cleanup',
        { conversationId: 'cv', turnId: 'tu' },
      )
      await new Promise((r) => setImmediate(r))
      await gate.handleExecuteApproved('approval-1')
      await promise
      expect(runRaw).toHaveBeenCalledWith(expect.any(Object), {
        conversationId: 'cv',
        turnId: 'tu',
      })
    })

    it('resolves with the runRaw failure result when the approved op fails', async () => {
      const { gate, runRaw } = build()
      runRaw.mockResolvedValueOnce({
        ok: false,
        outputTail: 'fatal: bad object\n',
        exitCode: 128,
      })
      const promise = gate.requestApproval(inv('Reset', ['reset', '--hard']), 'rb')
      await new Promise((r) => setImmediate(r))
      await gate.handleExecuteApproved('approval-1')
      const result = await promise
      expect(result).toEqual({ ok: false, outputTail: 'fatal: bad object\n' })
    })

    it('resolves with ok:false when runRaw throws', async () => {
      const { gate, runRaw } = build()
      runRaw.mockRejectedValueOnce(new Error('runner died'))
      const promise = gate.requestApproval(inv('Reset', ['reset', '--hard']), 'rb')
      await new Promise((r) => setImmediate(r))
      await gate.handleExecuteApproved('approval-1')
      const result = await promise
      expect(result.ok).toBe(false)
      expect(result.outputTail).toContain('runner died')
    })
  })

  describe('handleExecuteApproved', () => {
    it('logs a warn and no-ops on unknown approvalId', async () => {
      const { gate, runRaw, log } = build()
      await gate.handleExecuteApproved('does-not-exist')
      expect(runRaw).not.toHaveBeenCalled()
      expect(log.warn).toHaveBeenCalledWith(
        { approvalId: 'does-not-exist' },
        expect.stringContaining('unknown approvalId'),
      )
    })
  })

  describe('handleMergeBranch', () => {
    it('delegates to gitModule.merge with sourceBranch', async () => {
      const { gate, merge } = build()
      await gate.handleMergeBranch({
        sourceBranch: 'feature/x',
        targetBranch: 'main',
        requestedBy: 'alice',
      })
      expect(merge).toHaveBeenCalledTimes(1)
      expect(merge).toHaveBeenCalledWith('feature/x')
    })

    it('logs error and emits AssistantText carrier when merge throws', async () => {
      const { gate, merge, emitEvent, log } = build()
      merge.mockRejectedValueOnce(new Error('working tree dirty'))

      await gate.handleMergeBranch({
        sourceBranch: 'feature/x',
        targetBranch: 'main',
        requestedBy: 'alice',
      })

      expect(log.error).toHaveBeenCalled()

      // Drain microtask so the fire-and-forget emitEvent runs before assertion.
      await new Promise((r) => setImmediate(r))

      expect(emitEvent).toHaveBeenCalledTimes(1)
      const payload = emitEvent.mock.calls[0]?.[0] as EmitEventPayload
      expect(payload.sessionId).toBe('')
      expect(payload.kind).toBe('Status')
      const data = JSON.parse(payload.eventData)
      expect(data.type).toBe('server_merge_failed')
      expect(data.sourceBranch).toBe('feature/x')
      expect(data.targetBranch).toBe('main')
      expect(data.error).toContain('working tree dirty')
      expect(data.text).toContain('feature/x')
      expect(data.text).toContain('main')
    })
  })

  describe('expiry', () => {
    beforeEach(() => {
      vi.useFakeTimers()
    })

    afterEach(() => {
      vi.useRealTimers()
    })

    it('5min timer fires, emits AssistantText carrier, resolves promise with ok:false', async () => {
      const { gate, emitEvent, log } = build({ expiryMs: 300_000 })

      const promise = gate.requestApproval(
        inv('Reset', ['reset', '--hard', 'HEAD~1']),
        'rb',
      )
      // Drain the microtask that resolves invoke and parks the promise.
      await vi.advanceTimersByTimeAsync(0)

      // Just shy of expiry — still pending.
      await vi.advanceTimersByTimeAsync(299_999)

      // Cross the 5min boundary.
      await vi.advanceTimersByTimeAsync(2)

      const result = await promise
      expect(result).toEqual({ ok: false, outputTail: 'approval timeout' })

      expect(log.warn).toHaveBeenCalledWith(
        expect.objectContaining({ approvalId: 'approval-1', opType: 'Reset' }),
        expect.stringContaining('expired'),
      )

      // AssistantText carrier emitted.
      expect(emitEvent).toHaveBeenCalledTimes(1)
      const payload = emitEvent.mock.calls[0]?.[0] as EmitEventPayload
      expect(payload.sessionId).toBe('')
      expect(payload.kind).toBe('Status')
      const data = JSON.parse(payload.eventData)
      expect(data.type).toBe('destructive_op_expired')
      expect(data.opType).toBe('Reset')
      expect(data.text).toContain('Reset')
      expect(data.text).toContain('5 minutes')
    })

    it('execute-approved after expiry no-ops (entry already removed)', async () => {
      const { gate, runRaw } = build({ expiryMs: 300_000 })

      const promise = gate.requestApproval(inv('Reset', ['reset', '--hard']), 'rb')
      await vi.advanceTimersByTimeAsync(0)

      // Fire the expiry.
      await vi.advanceTimersByTimeAsync(300_001)
      await promise

      // Late approval: should be a no-op.
      await gate.handleExecuteApproved('approval-1')
      expect(runRaw).not.toHaveBeenCalled()
    })
  })

  describe('shutdown', () => {
    beforeEach(() => {
      vi.useFakeTimers()
    })

    afterEach(() => {
      vi.useRealTimers()
    })

    it('resolves all pending promises with ok:false and clears all timers', async () => {
      const { gate, invoke, runRaw } = build({ expiryMs: 300_000 })
      // Two distinct approvalIds.
      invoke.mockResolvedValueOnce({ approvalId: 'a-1' })
      invoke.mockResolvedValueOnce({ approvalId: 'a-2' })

      const p1 = gate.requestApproval(inv('Reset', ['reset', '--hard']), 'r1')
      const p2 = gate.requestApproval(inv('ForcePush', ['push', '--force']), 'r2')
      await vi.advanceTimersByTimeAsync(0)

      gate.shutdown()

      const [r1, r2] = await Promise.all([p1, p2])
      expect(r1).toEqual({ ok: false, outputTail: 'daemon shutting down' })
      expect(r2).toEqual({ ok: false, outputTail: 'daemon shutting down' })

      // Timers cleared: advancing past 5min must not trigger anything.
      await vi.advanceTimersByTimeAsync(300_001)
      expect(runRaw).not.toHaveBeenCalled()

      // Late approvals after shutdown: no-op (entries cleared).
      await gate.handleExecuteApproved('a-1')
      await gate.handleExecuteApproved('a-2')
      expect(runRaw).not.toHaveBeenCalled()
    })

    it('is idempotent — second shutdown is a no-op', () => {
      const { gate } = build()
      gate.shutdown()
      expect(() => gate.shutdown()).not.toThrow()
    })
  })
})
