// Tests for StartingServicesStage. Hand-rolled SupervisordController fake (just
// `addService`) and IExecutor fake (supervisorctl status + healthcheck shells).
// Vitest fake timers drive both the supervisorctl RUNNING poll and the active
// healthcheck poll.
//
// V2 cutover: payloads now carry `runtimeSpec.services` as a `ServiceSpec[]`
// (full shape — name + command + optional user/env/autorestart/healthcheck/
// install). The legacy `languages` map shim has been deleted from the stage,
// so every test below constructs a real ServiceSpec entry.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { ExecOpts, ExecResult, IExecutor } from '../../runtime/IExecutor.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type {
  BootstrapPayloadV2,
  BootstrapServiceSpec,
} from '../../signalr/types.js'

import { BootstrapState } from '../BootstrapState.js'
import { StartingServicesStage } from './StartingServicesStage.js'

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

function payloadWithServices(services: BootstrapServiceSpec[]): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: { version: 2, services },
    envVars: [],
    hooks: null,
    mcps: [],
    repo: null,
  }
}

function makeStatusExecutor(getStatus: () => string) {
  const calls: Array<{ command: string; args: readonly string[]; opts?: ExecOpts }> = []
  const run = vi.fn(
    async (command: string, args: readonly string[], opts?: ExecOpts): Promise<ExecResult> => {
      calls.push({ command, args, ...(opts !== undefined ? { opts } : {}) })
      if (command === 'supervisorctl' && args[0] === 'status') {
        return { stdout: getStatus(), stderr: '', exitCode: 0 }
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    },
  )
  const executor: IExecutor = { run }
  return { executor, run, calls }
}

describe('StartingServicesStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('skipped when services array is empty', async () => {
    const addService = vi.fn(async () => {})
    const { executor, run } = makeStatusExecutor(() => '')
    const state = new BootstrapState()
    state.setPayload(payloadWithServices([]))
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(addService).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })

  it('happy path: registers each service + waits until RUNNING', async () => {
    const addService = vi.fn(async () => {})
    let status = 'postgres                         STARTING\n'
    const { executor } = makeStatusExecutor(() => status)
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([
        { name: 'postgres', command: '/usr/bin/postgres', autorestart: true },
      ]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 5_000,
      healthPollIntervalMs: 100,
    })

    const promise = stage.run(makeContext())
    // First poll happens immediately — STARTING. Advance 100ms, flip to RUNNING.
    await vi.advanceTimersByTimeAsync(50)
    status = 'postgres                         RUNNING   pid 1234, uptime 0:00:05\n'
    await vi.advanceTimersByTimeAsync(200)

    const result = await promise
    expect(result).toEqual({ ok: true })
    expect(addService).toHaveBeenCalledTimes(1)
    expect(addService).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'postgres', command: '/usr/bin/postgres' }),
      expect.anything(),
    )
  })

  it('addService failure returns recoverable failure', async () => {
    const addService = vi.fn(async () => {
      throw new Error('supervisorctl reread failed')
    })
    const { executor } = makeStatusExecutor(() => '')
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([{ name: 'redis', command: '/usr/bin/redis-server' }]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/addService\(redis\)/)
      expect(result.reason).toMatch(/supervisorctl reread failed/)
    }
  })

  it('healthcheck timeout returns recoverable failure', async () => {
    const addService = vi.fn(async () => {})
    // Service stays STARTING forever — RUNNING poll will time out.
    const { executor } = makeStatusExecutor(() => 'redis                            STARTING\n')
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([{ name: 'redis', command: '/usr/bin/redis-server' }]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 500,
      healthPollIntervalMs: 100,
    })

    const promise = stage.run(makeContext())
    await vi.advanceTimersByTimeAsync(1_000)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/did not reach RUNNING/)
      expect(result.reason).toMatch(/redis/)
    }
  })

  it('pre-aborted signal returns recoverable failure without doing anything', async () => {
    const addService = vi.fn(async () => {})
    const { executor, run } = makeStatusExecutor(() => '')
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([{ name: 'postgres', command: '/usr/bin/postgres' }]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
    })

    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(addService).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })

  it('aborts during healthcheck poll loop when signal fires', async () => {
    const addService = vi.fn(async () => {})
    const { executor } = makeStatusExecutor(() => 'redis                            STARTING\n')
    const ac = new AbortController()
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([{ name: 'redis', command: '/usr/bin/redis-server' }]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 10_000,
      healthPollIntervalMs: 100,
    })

    const promise = stage.run(makeContext({ signal: ac.signal }))
    await vi.advanceTimersByTimeAsync(150)
    ac.abort()
    await vi.advanceTimersByTimeAsync(150)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.reason).toBe('aborted')
      expect(result.recoverable).toBe(true)
    }
  })

  it('registers every declared service (no language-map filtering — V2 contract)', async () => {
    const addService = vi.fn(async () => {})
    // Only postgres reports RUNNING — redis stays absent so the wait should
    // time out, but we still want to assert addService was called for BOTH.
    const { executor } = makeStatusExecutor(() => 'postgres                         RUNNING\n')
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([
        { name: 'postgres', command: '/usr/bin/postgres' },
        { name: 'redis', command: '/usr/bin/redis-server' },
      ]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 1_000,
      healthPollIntervalMs: 50,
    })

    const promise = stage.run(makeContext())
    await vi.advanceTimersByTimeAsync(2_000)
    const result = await promise
    expect(addService).toHaveBeenCalledTimes(2)
    const registered = (
      addService.mock.calls as unknown as Array<[{ name: string }, ...unknown[]]>
    ).map((c) => c[0].name)
    expect([...registered].sort()).toEqual(['postgres', 'redis'])
    // redis never RUNNING → recoverable failure.
    expect(result.ok).toBe(false)
  })

  it('runs active healthcheck command after RUNNING, succeeds on exit 0', async () => {
    const addService = vi.fn(async () => {})
    const calls: Array<{ command: string; args: readonly string[] }> = []
    const run = vi.fn(
      async (command: string, args: readonly string[]): Promise<ExecResult> => {
        calls.push({ command, args })
        if (command === 'supervisorctl' && args[0] === 'status') {
          return {
            stdout: 'postgres                         RUNNING   pid 1234, uptime 0:00:05\n',
            stderr: '',
            exitCode: 0,
          }
        }
        // Healthcheck shell — `sh -c pg_isready` → exit 0 first try.
        if (command === 'sh' && args[0] === '-c') {
          return { stdout: 'accepting connections', stderr: '', exitCode: 0 }
        }
        return { stdout: '', stderr: '', exitCode: 0 }
      },
    )
    const executor: IExecutor = { run }
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([
        {
          name: 'postgres',
          command: '/usr/bin/postgres',
          healthcheck: { command: 'pg_isready', intervalSeconds: 2 },
        },
      ]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 5_000,
      healthPollIntervalMs: 50,
    })

    const promise = stage.run(makeContext())
    await vi.advanceTimersByTimeAsync(200)
    const result = await promise
    expect(result).toEqual({ ok: true })

    // The healthcheck command was invoked at least once via `sh -c`.
    const probeCalls = calls.filter((c) => c.command === 'sh' && c.args[0] === '-c')
    expect(probeCalls.length).toBeGreaterThanOrEqual(1)
    expect(probeCalls[0]?.args[1]).toBe('pg_isready')
  })

  it('healthcheck never succeeds → stage still ok (soft-fail advisory)', async () => {
    // Runtime-spec-v2 healthcheck softening: a failing post-RUNNING probe
    // must NOT fail the stage anymore (process-alive is the hard gate; the
    // active healthcheck is best-effort). The stage emits a
    // `ServiceHealthcheckTimedOut` Warn for observability and continues to
    // Online — that's what this test asserts.
    const addService = vi.fn(async () => {})
    const run = vi.fn(
      async (command: string, args: readonly string[]): Promise<ExecResult> => {
        if (command === 'supervisorctl' && args[0] === 'status') {
          return {
            stdout: 'postgres                         RUNNING   pid 1234, uptime 0:00:05\n',
            stderr: '',
            exitCode: 0,
          }
        }
        // Healthcheck always fails.
        if (command === 'sh' && args[0] === '-c') {
          return { stdout: '', stderr: 'not ready', exitCode: 1 }
        }
        return { stdout: '', stderr: '', exitCode: 0 }
      },
    )
    const executor: IExecutor = { run }
    const state = new BootstrapState()
    state.setPayload(
      payloadWithServices([
        {
          name: 'postgres',
          command: '/usr/bin/postgres',
          healthcheck: { command: 'pg_isready', intervalSeconds: 1 },
        },
      ]),
    )
    const stage = new StartingServicesStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      supervisord: {
        addService,
        reconcileServices: vi.fn(async () => []),
        listConfiguredServiceNames: vi.fn(async () => []),
      },
      executor,
      healthTimeoutMs: 300,
      healthPollIntervalMs: 50,
    })

    const promise = stage.run(makeContext())
    await vi.advanceTimersByTimeAsync(1_000)
    const result = await promise
    // Stage now succeeds even when the healthcheck never returns exit 0 —
    // process-alive (RUNNING) is the only hard gate.
    expect(result).toEqual({ ok: true })
  })

  // ===========================================================================
  // Pre-flight required-env guard (layer-2 hardening)
  // ===========================================================================
  describe('required-env pre-flight guard', () => {
    /** Capturing emitter fake — records every emit() call for assertions. */
    function makeEmitter() {
      const emit = vi.fn()
      const emitter = {
        emit,
        startTimer: vi.fn(() => ({ complete: vi.fn(), fail: vi.fn(), skip: vi.fn() })),
      }
      return { emitter, emit }
    }

    /** envVarManager fake backed by a plain object of key→value. */
    function makeEnv(entries: Record<string, string>) {
      const map = new Map(Object.entries(entries))
      return { current: () => map as ReadonlyMap<string, string> }
    }

    /** ServiceSpec carrying requiredEnv (not on the generated wire type yet). */
    function svc(
      name: string,
      requiredEnv: Array<{ key: string; secret?: boolean }>,
    ): BootstrapServiceSpec {
      return {
        name,
        command: `/usr/bin/${name}`,
        autorestart: true,
        requiredEnv,
      } as unknown as BootstrapServiceSpec
    }

    it('skips a service whose required env var is missing and emits ServiceEnvMissing', async () => {
      const addService = vi.fn(async () => {})
      const { executor } = makeStatusExecutor(() => '')
      const { emitter, emit } = makeEmitter()
      const state = new BootstrapState()
      state.setPayload(
        payloadWithServices([
          svc('worker', [{ key: 'QUEUE_URL' }, { key: 'OPENROUTER_API_KEY', secret: true }]),
        ]),
      )
      const stage = new StartingServicesStage({
        signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
        state,
        supervisord: {
          addService,
          reconcileServices: vi.fn(async () => []),
          listConfiguredServiceNames: vi.fn(async () => []),
        },
        executor,
        emitter,
        envVarManager: makeEnv({}), // nothing set → both keys missing
        healthTimeoutMs: 300,
        healthPollIntervalMs: 50,
      })

      const promise = stage.run(makeContext())
      await vi.advanceTimersByTimeAsync(500)
      const result = await promise

      // Runtime still comes up — the missing-env service is skipped, not fatal.
      expect(result).toEqual({ ok: true })
      // Never registered with supervisord (no crash-loop).
      expect(addService).not.toHaveBeenCalled()
      // Exactly one ServiceEnvMissing event, Warn, listing both missing keys.
      const envMissing = emit.mock.calls.filter((c) => c[0] === 'ServiceEnvMissing')
      expect(envMissing).toHaveLength(1)
      expect(envMissing[0][1]).toBe('Warn')
      expect(envMissing[0][2]).toEqual({
        serviceName: 'worker',
        missingEnvVars: ['QUEUE_URL', 'OPENROUTER_API_KEY'],
      })
    })

    it('treats an empty-string env value as missing', async () => {
      const addService = vi.fn(async () => {})
      const { executor } = makeStatusExecutor(() => '')
      const { emit, emitter } = makeEmitter()
      const state = new BootstrapState()
      state.setPayload(payloadWithServices([svc('api', [{ key: 'API_KEY' }])]))
      const stage = new StartingServicesStage({
        signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
        state,
        supervisord: {
          addService,
          reconcileServices: vi.fn(async () => []),
          listConfiguredServiceNames: vi.fn(async () => []),
        },
        executor,
        emitter,
        envVarManager: makeEnv({ API_KEY: '' }), // present but empty
        healthTimeoutMs: 300,
        healthPollIntervalMs: 50,
      })

      const promise = stage.run(makeContext())
      await vi.advanceTimersByTimeAsync(500)
      const result = await promise

      expect(result).toEqual({ ok: true })
      expect(addService).not.toHaveBeenCalled()
      const envMissing = emit.mock.calls.filter((c) => c[0] === 'ServiceEnvMissing')
      expect(envMissing).toHaveLength(1)
      expect(envMissing[0][2]).toEqual({ serviceName: 'api', missingEnvVars: ['API_KEY'] })
    })

    it('registers the service when all required env vars are present', async () => {
      const addService = vi.fn(async () => {})
      let status = 'api                              STARTING\n'
      const { executor } = makeStatusExecutor(() => status)
      const { emit, emitter } = makeEmitter()
      const state = new BootstrapState()
      state.setPayload(
        payloadWithServices([svc('api', [{ key: 'OPENROUTER_API_KEY', secret: true }])]),
      )
      const stage = new StartingServicesStage({
        signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
        state,
        supervisord: {
          addService,
          reconcileServices: vi.fn(async () => []),
          listConfiguredServiceNames: vi.fn(async () => []),
        },
        executor,
        emitter,
        envVarManager: makeEnv({ OPENROUTER_API_KEY: 'sk-or-xyz' }),
        healthTimeoutMs: 5_000,
        healthPollIntervalMs: 100,
      })

      const promise = stage.run(makeContext())
      await vi.advanceTimersByTimeAsync(50)
      status = 'api                              RUNNING   pid 1234, uptime 0:00:05\n'
      await vi.advanceTimersByTimeAsync(200)
      const result = await promise

      expect(result).toEqual({ ok: true })
      expect(addService).toHaveBeenCalledTimes(1)
      expect(emit.mock.calls.filter((c) => c[0] === 'ServiceEnvMissing')).toHaveLength(0)
    })

    it('starts only the satisfied service when one of two is missing env', async () => {
      const addService = vi.fn(async () => {})
      let status = 'api                              STARTING\n'
      const { executor } = makeStatusExecutor(() => status)
      const { emit, emitter } = makeEmitter()
      const state = new BootstrapState()
      state.setPayload(
        payloadWithServices([
          svc('api', [{ key: 'OPENROUTER_API_KEY', secret: true }]),
          svc('worker', [{ key: 'QUEUE_URL' }]),
        ]),
      )
      const stage = new StartingServicesStage({
        signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
        state,
        supervisord: {
          addService,
          reconcileServices: vi.fn(async () => []),
          listConfiguredServiceNames: vi.fn(async () => []),
        },
        executor,
        emitter,
        envVarManager: makeEnv({ OPENROUTER_API_KEY: 'sk-or-xyz' }), // QUEUE_URL absent
        healthTimeoutMs: 5_000,
        healthPollIntervalMs: 100,
      })

      const promise = stage.run(makeContext())
      await vi.advanceTimersByTimeAsync(50)
      status = 'api                              RUNNING   pid 1234, uptime 0:00:05\n'
      await vi.advanceTimersByTimeAsync(200)
      const result = await promise

      // 'api' starts (and reaches RUNNING); 'worker' is skipped — the stage does
      // NOT wait the full deadline for worker (proving it's excluded from the
      // pending set), so the run resolves promptly with ok.
      expect(result).toEqual({ ok: true })
      expect(addService).toHaveBeenCalledTimes(1)
      expect(addService).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'api' }),
        expect.anything(),
      )
      const envMissing = emit.mock.calls.filter((c) => c[0] === 'ServiceEnvMissing')
      expect(envMissing).toHaveLength(1)
      expect(envMissing[0][2]).toEqual({ serviceName: 'worker', missingEnvVars: ['QUEUE_URL'] })
    })

    it('guard is disabled (service registered) when no envVarManager is injected', async () => {
      const addService = vi.fn(async () => {})
      let status = 'api                              STARTING\n'
      const { executor } = makeStatusExecutor(() => status)
      const state = new BootstrapState()
      state.setPayload(payloadWithServices([svc('api', [{ key: 'QUEUE_URL' }])]))
      const stage = new StartingServicesStage({
        signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
        state,
        supervisord: {
          addService,
          reconcileServices: vi.fn(async () => []),
          listConfiguredServiceNames: vi.fn(async () => []),
        },
        executor,
        // no envVarManager → guard off, back-compat with older tests
        healthTimeoutMs: 5_000,
        healthPollIntervalMs: 100,
      })

      const promise = stage.run(makeContext())
      await vi.advanceTimersByTimeAsync(50)
      status = 'api                              RUNNING   pid 1234, uptime 0:00:05\n'
      await vi.advanceTimersByTimeAsync(200)
      const result = await promise

      expect(result).toEqual({ ok: true })
      expect(addService).toHaveBeenCalledTimes(1)
    })
  })
})
