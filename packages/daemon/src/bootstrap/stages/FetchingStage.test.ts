// Tests for FetchingStage. Hand-rolled SignalR fake (just `getBootstrap` +
// `reportBootstrapProgress`); no `vi.mock` of `@microsoft/signalr`.
//
// V2 cutover: the stage now expects `version === 'v2'` and a `runtimeSpec`
// shaped as `RuntimeSpecV2` (services[], setup bash, install bash).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'

import { BootstrapState } from '../BootstrapState.js'
import { FetchingStage } from './FetchingStage.js'

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

function validPayload(): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: {
      version: 2,
      services: [],
      setup: 'npm ci',
    },
    envVars: [{ key: 'FOO', value: 'bar' }],
    hooks: null,
    mcps: [{ name: 'github', url: 'http://api/github', scope: 'opaque' }],
    repo: null,
  }
}

describe('FetchingStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('happy path: fetches payload and stashes it in state', async () => {
    const payload = validPayload()
    const getBootstrap = vi.fn(async () => payload)
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(state.payload).toBe(payload)
    expect(getBootstrap).toHaveBeenCalledTimes(1)
  })

  it('non-v2 payload version returns non-recoverable failure', async () => {
    const payload = { ...validPayload(), version: 'v999' }
    const getBootstrap = vi.fn(async () => payload as BootstrapPayloadV2)
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/version_mismatch/)
    }
    expect(state.hasPayload()).toBe(false)
  })

  it('legacy v1 payload version returns non-recoverable failure (daemon expects v2 only)', async () => {
    const payload = { ...validPayload(), version: 'v1' }
    const getBootstrap = vi.fn(async () => payload as BootstrapPayloadV2)
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/version_mismatch/)
    }
  })

  it('missing version field returns non-recoverable failure', async () => {
    const getBootstrap = vi.fn(async () => ({}) as BootstrapPayloadV2)
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.recoverable).toBe(false)
  })

  it('malformed payload (missing runtimeSpec) returns non-recoverable failure', async () => {
    const getBootstrap = vi.fn(
      async () =>
        ({
          version: 'v2',
          envVars: [],
          mcps: [],
          hooks: null,
          repo: null,
        }) as unknown as BootstrapPayloadV2,
    )
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/shape invalid/)
    }
  })

  it('signalr.getBootstrap rejects → recoverable failure (orchestrator retries)', async () => {
    const getBootstrap = vi.fn(async () => {
      throw new Error('hub closed')
    })
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toContain('hub closed')
    }
  })

  it('times out when getBootstrap hangs longer than timeoutMs', async () => {
    let neverResolve: (v: BootstrapPayloadV2) => void = () => {}
    const getBootstrap = vi.fn(
      () =>
        new Promise<BootstrapPayloadV2>((resolve) => {
          neverResolve = resolve
        }),
    )
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
      timeoutMs: 100,
    })
    const promise = stage.run(makeContext())
    await vi.advanceTimersByTimeAsync(200)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/timed out/)
    }
    // Settle the dangling promise so vitest doesn't complain.
    neverResolve(validPayload())
  })

  it('aborts when signal fires during fetch', async () => {
    let neverResolve: (v: BootstrapPayloadV2) => void = () => {}
    const getBootstrap = vi.fn(
      () =>
        new Promise<BootstrapPayloadV2>((resolve) => {
          neverResolve = resolve
        }),
    )
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    const ac = new AbortController()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
      timeoutMs: 10_000,
    })
    const promise = stage.run(makeContext({ signal: ac.signal }))
    await vi.advanceTimersByTimeAsync(50)
    ac.abort()
    await vi.advanceTimersByTimeAsync(50)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.reason).toMatch(/aborted/)
    }
    neverResolve(validPayload())
  })

  it('pre-aborted signal returns recoverable failure without calling getBootstrap', async () => {
    const getBootstrap = vi.fn(async () => validPayload())
    const reportBootstrapProgress = vi.fn(async () => {})
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    const stage = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(getBootstrap).not.toHaveBeenCalled()
  })
})
