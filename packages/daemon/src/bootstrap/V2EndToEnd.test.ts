// Smoke / end-to-end logic test for the V2 wire-through.
//
// Card 32b0481b (P1 wiring: Daemon V2 spec through bootstrap stages +
// RuntimeSpecApplier) requires a smoke pass that proves the V2 payload shape
// flows correctly:
//
//   1. FetchingStage accepts `version: 'v2'` + `runtimeSpec: RuntimeSpecV2`
//      (services[] + setup bash) and stashes it in shared BootstrapState.
//   2. StartingServicesStage iterates `payload.runtimeSpec.services` and
//      hands each full `ServiceSpec` to `supervisord.addService` — no
//      language-map filtering, no shim catalog.
//   3. RunningSetupStage executes the freeform `setup` bash string via
//      `bash -c`, NOT the old per-line `sh -c` loop.
//   4. RuntimeSpecApplier walks the V2 delta and applies install→services→
//      setup in order; services are registered + supervisorctl-restarted;
//      setup is bash-c'd.
//
// We hand-roll every collaborator (signalr / supervisord / executor) so this
// is a unit-level smoke pass, not a real network/process test.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import { TestRuntimeEventEmitter } from '../events/RuntimeEventEmitter.js'
import type { ExecOpts, ExecResult, IExecutor } from '../runtime/IExecutor.js'
import {
  InstallHashStore,
  type InstallHashStoreFs,
} from '../runtime/InstallHashStore.js'
import { RuntimeSpecApplier } from '../runtime/RuntimeSpecApplier.js'
import type {
  ServiceSpec as ControllerServiceSpec,
  SupervisordController,
} from '../runtime/SupervisordController.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  ApplyRuntimeSpecDeltaPayload,
  BootstrapPayloadV2,
} from '../signalr/types.js'

import type { BootstrapContext } from './BootstrapOrchestrator.js'
import { BootstrapState } from './BootstrapState.js'
import { FetchingStage } from './stages/FetchingStage.js'
import { RunningSetupStage } from './stages/RunningSetupStage.js'
import { StartingServicesStage } from './stages/StartingServicesStage.js'

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

interface CapturedCall {
  command: string
  args: readonly string[]
  opts?: ExecOpts
}

function makeExecutor(
  impl: (command: string, args: readonly string[]) => ExecResult | Promise<ExecResult>,
) {
  const calls: CapturedCall[] = []
  const run = vi.fn(
    async (command: string, args: readonly string[], opts?: ExecOpts): Promise<ExecResult> => {
      calls.push({ command, args, ...(opts !== undefined ? { opts } : {}) })
      return impl(command, args)
    },
  )
  const executor: IExecutor = { run }
  return { executor, calls }
}

describe('V2 end-to-end smoke', () => {
  it('bootstrap: fetch → services → setup wires a postgres+setup spec all the way through', async () => {
    // The V2 payload the backend would emit for a project declaring a
    // postgres service and a setup script.
    const payload: BootstrapPayloadV2 = {
      version: 'v2',
      runtimeSpec: {
        version: 2,
        services: [
          {
            name: 'postgres',
            command: '/usr/lib/postgresql/16/bin/postgres -D /var/lib/postgresql/data',
            user: 'postgres',
            autorestart: true,
            healthcheck: { command: 'pg_isready -h 127.0.0.1', intervalSeconds: 1 },
          },
        ],
        setup: 'npm ci && npm run db:migrate',
      },
      envVars: [],
      hooks: null,
      mcps: [],
      repo: null,
    }

    const addService = vi.fn(async (_spec: ControllerServiceSpec) => {})
    const reconcileServices = vi.fn(async () => [] as string[])
    const listConfiguredServiceNames = vi.fn(async () => [] as string[])
    const supervisord = { addService, reconcileServices, listConfiguredServiceNames }

    // Executor handles every shell-out the stages produce. Each branch is
    // explicit so a stray exec call would show up as an unknown invocation.
    let pgReadyTries = 0
    const { executor, calls } = makeExecutor(async (command, args) => {
      if (command === 'supervisorctl' && args[0] === 'status') {
        return {
          stdout: 'postgres                         RUNNING   pid 4321, uptime 0:00:02\n',
          stderr: '',
          exitCode: 0,
        }
      }
      // First probe pretends Postgres is still booting; the second is healthy.
      if (command === 'sh' && args[0] === '-c' && args[1]?.startsWith('pg_isready')) {
        pgReadyTries += 1
        return pgReadyTries === 1
          ? { stdout: '', stderr: 'connecting', exitCode: 2 }
          : { stdout: 'accepting connections', stderr: '', exitCode: 0 }
      }
      if (command === 'bash' && args[0] === '-c') {
        return { stdout: 'migrations ok', stderr: '', exitCode: 0 }
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })

    const state = new BootstrapState()
    const reportBootstrapProgress = vi.fn(async () => {})

    // === Stage 1: Fetching ===
    const getBootstrap = vi.fn(async () => payload)
    const fetching = new FetchingStage({
      signalr: { getBootstrap, reportBootstrapProgress },
      state,
    })
    const fetchResult = await fetching.run(makeContext())
    expect(fetchResult).toEqual({ ok: true })
    expect(state.hasPayload()).toBe(true)
    expect(state.payload.runtimeSpec.services?.[0]?.name).toBe('postgres')

    // === Stage 2: StartingServices ===
    const emitter = new TestRuntimeEventEmitter()
    const services = new StartingServicesStage({
      signalr: { reportBootstrapProgress },
      state,
      supervisord: supervisord as unknown as Pick<
        SupervisordController,
        'addService' | 'reconcileServices' | 'listConfiguredServiceNames'
      >,
      executor,
      emitter,
      healthTimeoutMs: 5_000,
      healthPollIntervalMs: 50,
    })
    const servicesResult = await services.run(makeContext())
    expect(servicesResult).toEqual({ ok: true })
    expect(addService).toHaveBeenCalledTimes(1)
    expect(addService).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'postgres',
        command: expect.stringContaining('postgres'),
        user: 'postgres',
      }),
      expect.anything(),
    )
    // Active healthcheck ran at least once via `sh -c pg_isready ...`.
    const probeCalls = calls.filter(
      (c) => c.command === 'sh' && c.args[0] === '-c' && c.args[1]?.startsWith('pg_isready'),
    )
    expect(probeCalls.length).toBeGreaterThanOrEqual(1)

    // === Stage 3: RunningSetup ===
    const setup = new RunningSetupStage({
      signalr: { reportBootstrapProgress },
      state,
      executor,
      emitter,
      repoDir: '/data/project/repo',
    })
    const setupResult = await setup.run(makeContext())
    expect(setupResult).toEqual({ ok: true })
    // The setup bash hit bash -c (not sh -c) in /data/project/repo with PATH
    // including mise.
    const bashCalls = calls.filter((c) => c.command === 'bash' && c.args[0] === '-c')
    expect(bashCalls).toHaveLength(1)
    expect(bashCalls[0]?.args[1]).toBe('npm ci && npm run db:migrate')
    expect(bashCalls[0]?.opts?.cwd).toBe('/data/project/repo')
    expect(bashCalls[0]?.opts?.env?.['PATH']).toContain('/data/mise/shims')

    // === Structured RuntimeEvent assertions ===
    //
    // The expected sequence is:
    //   ServiceStarting(postgres)
    //   ServiceRunning(postgres, durationMs)
    //   SetupCommandStarted(...)
    //   SetupCommandCompleted(...)
    //
    // Each *Completed / *Running event must carry a non-null durationMs.
    const eventTypes = emitter.events.map((e) => e.type)
    expect(eventTypes).toEqual([
      'ServiceStarting',
      'ServiceRunning',
      'SetupCommandStarted',
      'SetupCommandCompleted',
    ])

    const running = emitter.events.find((e) => e.type === 'ServiceRunning')!
    expect(running.payload['serviceName']).toBe('postgres')
    expect(running.payload['durationMs']).toBeTypeOf('number')

    const setupCompleted = emitter.events.find((e) => e.type === 'SetupCommandCompleted')!
    expect(setupCompleted.durationMs).toBeTypeOf('number')
    expect(setupCompleted.durationMs!).toBeGreaterThanOrEqual(0)

    // Severity defaults: Started / Running / Completed are all Info.
    for (const ev of emitter.events) {
      expect(ev.severity).toBe('Info')
    }
  })

  it('runtime-curation delta: install bash + services + setup all execute', async () => {
    // The applier walks a V2 delta: install (hash-gated bash -c), services
    // (addService + supervisorctl restart), setup (bash -c). Removals are
    // warn-only.
    const addService = vi.fn(async (_spec: ControllerServiceSpec) => {})
    const removeService = vi.fn(async (_name: string) => false)

    const acks: Array<{ proposalId: string; success: boolean; error?: string }> = []
    const invoke = vi.fn(async (method: string, payload: unknown) => {
      if (method === 'RuntimeSpecDeltaApplied') {
        acks.push(payload as { proposalId: string; success: boolean; error?: string })
      }
      return undefined
    })

    const { executor, calls } = makeExecutor(async () => ({
      stdout: '',
      stderr: '',
      exitCode: 0,
    }))

    // In-memory hash store — starts empty so the install hash differs and
    // the applier executes the install bash.
    let hashFileBody: string | undefined
    const hashFs: InstallHashStoreFs = {
      readFile: (async (_p: unknown) => {
        if (hashFileBody === undefined) {
          throw Object.assign(new Error('ENOENT'), { code: 'ENOENT' })
        }
        return hashFileBody
      }) as unknown as InstallHashStoreFs['readFile'],
      writeFile: (async (_p: unknown, body: unknown) => {
        hashFileBody = String(body)
      }) as unknown as InstallHashStoreFs['writeFile'],
      rename: (async () => {}) as unknown as InstallHashStoreFs['rename'],
    }
    const hashStore = new InstallHashStore({ path: '/tmp/test-hashes.json', fs: hashFs })

    const applierEmitter = new TestRuntimeEventEmitter()
    const applier = new RuntimeSpecApplier({
      signalr: { invoke } as unknown as SignalRClient,
      supervisord: { addService, removeService } as unknown as SupervisordController,
      executor,
      logger: makeLogger() as unknown as Logger,
      hashStore,
      emitter: applierEmitter,
    })

    const deltaPayload: ApplyRuntimeSpecDeltaPayload = {
      proposalId: 'smoke-1',
      delta: {
        newOrChangedServices: [
          {
            name: 'redis',
            command: '/usr/bin/redis-server --port 6379',
            autorestart: true,
          },
        ],
        removedServices: ['legacy-mailhog'],
        installChanged: true,
        installNew: 'apt-get install -y curl',
        setupChanged: true,
        setupNew: 'npm run migrate',
        hasChanges: true,
      },
    }

    await applier.applyDelta(deltaPayload)

    // Service registered.
    expect(addService).toHaveBeenCalledTimes(1)
    expect(addService).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'redis' }),
      undefined,
    )

    // Service restart issued (shape-change semantics).
    const restartCalls = calls.filter(
      (c) => c.command === 'supervisorctl' && c.args[0] === 'restart',
    )
    expect(restartCalls).toHaveLength(1)
    expect(restartCalls[0]?.args).toEqual(['restart', 'redis'])

    // Two bash -c calls: install bash (top-level), then setup bash.
    const bashCalls = calls.filter((c) => c.command === 'bash' && c.args[0] === '-c')
    expect(bashCalls).toHaveLength(2)
    expect(bashCalls[0]?.args[1]).toBe('apt-get install -y curl')
    expect(bashCalls[1]?.args[1]).toBe('npm run migrate')

    // Install hash persisted.
    expect(hashFileBody).toBeDefined()

    // Successful ack.
    expect(acks).toHaveLength(1)
    expect(acks[0]).toEqual({ proposalId: 'smoke-1', success: true })

    // === Structured RuntimeEvent assertions ===
    //
    // The applier should have emitted a paired SpecDeltaApplied (Started/
    // Completed) with phaseTimings on the completion event.
    const applierTypes = applierEmitter.events.map((e) => e.type)
    expect(applierTypes).toEqual(['SpecDeltaApplied', 'SpecDeltaApplied'])

    const startEvent = applierEmitter.events[0]!
    const completedEvent = applierEmitter.events[1]!
    expect(startEvent.payload['startedAt']).toBeTypeOf('string')
    expect(startEvent.payload['proposalId']).toBe('smoke-1')
    expect(startEvent.payload['deltaSummary']).toEqual({
      servicesAdded: 1,
      servicesRemoved: 1,
      installChanged: true,
      setupChanged: true,
    })

    expect(completedEvent.durationMs).toBeTypeOf('number')
    expect(completedEvent.severity).toBe('Info')
    const phaseTimings = completedEvent.payload['phaseTimings'] as {
      installMs: number
      servicesMs: number
      setupMs: number
    }
    expect(phaseTimings).toBeDefined()
    expect(phaseTimings.installMs).toBeTypeOf('number')
    expect(phaseTimings.servicesMs).toBeTypeOf('number')
    expect(phaseTimings.setupMs).toBeTypeOf('number')
  })
})
