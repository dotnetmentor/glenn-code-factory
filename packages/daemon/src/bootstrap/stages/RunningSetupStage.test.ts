// Tests for RunningSetupStage. Hand-rolled IExecutor fake so nothing actually
// shells out.
//
// V2 cutover: `runtimeSpec.setup` is now a single freeform bash string (not an
// array). The stage hands it to `bash -c` verbatim. Empty / whitespace-only
// input is skipped.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { ExecOpts, ExecResult, IExecutor } from '../../runtime/IExecutor.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'

import { BootstrapState } from '../BootstrapState.js'
import { RunningSetupStage } from './RunningSetupStage.js'

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

function makeContext(opts: { signal?: AbortSignal } = {}): BootstrapContext {
  return {
    config: {} as DaemonConfig,
    signalr: {} as SignalRClient,
    logger: makeLogger() as unknown as Logger,
    signal: opts.signal ?? new AbortController().signal,
  }
}

function payloadWithSetup(setup: string | undefined): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: { version: 2, ...(setup !== undefined ? { setup } : {}) },
    envVars: [],
    hooks: null,
    mcps: [],
    repo: null,
  }
}

function makeExecutor(impl?: (cmd: string, args: readonly string[]) => Promise<ExecResult>) {
  const calls: Array<{ command: string; args: readonly string[]; opts?: ExecOpts }> = []
  const run = vi.fn(
    async (command: string, args: readonly string[], opts?: ExecOpts): Promise<ExecResult> => {
      calls.push({ command, args, ...(opts !== undefined ? { opts } : {}) })
      if (impl) return impl(command, args)
      return { stdout: '', stderr: '', exitCode: 0 }
    },
  )
  const executor: IExecutor = { run }
  return { executor, run, calls }
}

describe('RunningSetupStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('happy path: runs the setup bash via `bash -c` in repoDir', async () => {
    const { executor, calls } = makeExecutor()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup('npm install && npm run build'))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(calls).toHaveLength(1)
    expect(calls[0]?.command).toBe('bash')
    expect(calls[0]?.args).toEqual(['-c', 'npm install && npm run build'])
    expect(calls[0]?.opts?.cwd).toBe('/data/project/repo')
  })

  it('PATH includes mise shims', async () => {
    const { executor, calls } = makeExecutor()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup('echo hi'))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    await stage.run(makeContext())
    expect(calls[0]?.opts?.env?.['PATH']).toContain('/data/mise/shims')
  })

  it('skipped when setup is undefined', async () => {
    const { executor, run } = makeExecutor()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup(undefined))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(run).not.toHaveBeenCalled()
  })

  it('skipped when setup is empty string', async () => {
    const { executor, run } = makeExecutor()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup(''))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(run).not.toHaveBeenCalled()
  })

  it('skipped when setup is whitespace only', async () => {
    const { executor, run } = makeExecutor()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup('   \n\t  \n'))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(run).not.toHaveBeenCalled()
  })

  it('command failure returns recoverable failure', async () => {
    const { executor } = makeExecutor(async () => {
      throw new Error('exit 1: build failed')
    })
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup('npm install && npm run build'))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/build failed/)
    }
  })

  it('pre-aborted signal returns recoverable failure without running anything', async () => {
    const { executor, run } = makeExecutor()
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    state.setPayload(payloadWithSetup('npm install'))
    const stage = new RunningSetupStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
    })

    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(run).not.toHaveBeenCalled()
  })
})
