// Tests for RuntimeSpecApplier. Hand-rolled fakes for SupervisordController,
// IExecutor, SignalRClient, and InstallHashStore — no real shell-outs, no
// real wire, no real disk.
//
// V2 cutover: the applier consumes `ApplyRuntimeSpecDeltaPayload` carrying a
// `delta: RuntimeSpecDeltaV2` with four buckets:
//   - newOrChangedServices: ServiceSpec[]   (per-service install gated by hash)
//   - removedServices: string[]             (Phase-2 reconciling: removeService per name)
//   - installChanged / installNew           (top-level install, gated by hash)
//   - setupChanged / setupNew               (re-run via bash -c)

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  ApplyRuntimeSpecDeltaPayload,
  RuntimeSpecDeltaApplyResultPayload,
  RuntimeSpecDeltaV2,
} from '../signalr/types.js'
import type { ExecOpts, ExecResult, IExecutor } from './IExecutor.js'

import {
  InstallHashStore,
  sha256Hex,
  type InstallHashStoreFs,
} from './InstallHashStore.js'
import { RuntimeSpecApplier } from './RuntimeSpecApplier.js'
import type { ServiceSpec, SupervisordController } from './SupervisordController.js'

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

interface SupervisordStub {
  addService: ReturnType<typeof vi.fn>
  removeService: ReturnType<typeof vi.fn>
  controller: SupervisordController
}
function makeSupervisord(impl?: (svc: { name: string }) => Promise<void>): SupervisordStub {
  const addService = vi.fn(async (svc: { name: string }) => {
    if (impl) await impl(svc)
  })
  const removeService = vi.fn(async (_name: string) => false)
  const controller = { addService, removeService } as unknown as SupervisordController
  return { addService, removeService, controller }
}

interface SignalRStub {
  invoke: ReturnType<typeof vi.fn>
  client: SignalRClient
  ackCalls(): RuntimeSpecDeltaApplyResultPayload[]
}
function makeSignalR(): SignalRStub {
  const invoke = vi.fn(async (_method: string, _payload: unknown) => undefined)
  const client = { invoke } as unknown as SignalRClient
  return {
    invoke,
    client,
    ackCalls() {
      return invoke.mock.calls
        .filter((c) => c[0] === 'RuntimeSpecDeltaApplied')
        .map((c) => c[1] as RuntimeSpecDeltaApplyResultPayload)
    },
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

/**
 * In-memory `InstallHashStore` backed by a tiny fs map — no real disk
 * I/O. `initial` seeds the file as if a prior boot had already cached
 * those hashes; omit it for a "fresh" store (read returns empty).
 *
 * `current()` reads back what the store currently has on disk (post any
 * applier writes), which is how tests assert "this scope's hash got
 * updated" or "this scope's hash did NOT update".
 */
interface HashStoreStub {
  store: InstallHashStore
  current(): { topLevel: string; services: Record<string, string> }
  path: string
}
function makeHashStore(opts: {
  initial?: { topLevel: string; services: Record<string, string> }
} = {}): HashStoreStub {
  const path = '/data/.glenn/install-hashes.json'
  // Single-cell "file system" — the path is the only key we care about.
  let contents: string | undefined =
    opts.initial !== undefined
      ? JSON.stringify(opts.initial)
      : undefined
  const fs: InstallHashStoreFs = {
    readFile: (async (p: unknown) => {
      void p
      if (contents === undefined) {
        const err = Object.assign(new Error('ENOENT'), { code: 'ENOENT' })
        throw err
      }
      return contents
    }) as unknown as InstallHashStoreFs['readFile'],
    writeFile: (async (p: unknown, body: unknown) => {
      // Mirrors atomic-write — the store writes to <path>.tmp first; we
      // just record the body and let rename promote it.
      void p
      contents = String(body)
    }) as unknown as InstallHashStoreFs['writeFile'],
    rename: (async (_from: unknown, _to: unknown) => {
      void _from
      void _to
      // No-op: writeFile already promoted in our single-cell model.
    }) as unknown as InstallHashStoreFs['rename'],
  }
  const store = new InstallHashStore({ path, fs })
  return {
    store,
    path,
    current() {
      if (contents === undefined) return { topLevel: '', services: {} }
      const parsed = JSON.parse(contents) as {
        topLevel: string
        services: Record<string, string>
      }
      return parsed
    },
  }
}

function makeApplier(opts: {
  supervisord: SupervisordController
  signalr: SignalRClient
  executor?: IExecutor
  hashStore?: InstallHashStore
}) {
  return new RuntimeSpecApplier({
    signalr: opts.signalr,
    supervisord: opts.supervisord,
    executor: opts.executor ?? makeExecutor().executor,
    logger: makeLogger() as unknown as Logger,
    // Tests that don't care about install behaviour get a fresh in-memory
    // store so the default constructor doesn't try to touch /data/.glenn.
    hashStore: opts.hashStore ?? makeHashStore().store,
  })
}

function emptyDelta(): RuntimeSpecDeltaV2 {
  return {
    newOrChangedServices: [],
    removedServices: [],
    installChanged: false,
    setupChanged: false,
    hasChanges: false,
  }
}

function payload(p: {
  proposalId?: string
  delta?: Partial<RuntimeSpecDeltaV2>
}): ApplyRuntimeSpecDeltaPayload {
  return {
    proposalId: p.proposalId ?? 'proposal-1',
    delta: { ...emptyDelta(), ...(p.delta ?? {}) },
  }
}

const SVC_REDIS: ServiceSpec = { name: 'redis', command: '/usr/bin/redis-server' }
const SVC_POSTGRES: ServiceSpec = { name: 'postgres', command: '/usr/bin/postgres' }

// ============================================================================
// Tests
// ============================================================================

describe('RuntimeSpecApplier.applyDelta', () => {
  it('happy path: new service registered + ack success', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'p1',
        delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
      }),
    )

    expect(supervisord.addService).toHaveBeenCalledTimes(1)
    expect(supervisord.addService).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'redis' }),
      undefined,
    )

    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(1)
    expect(acks[0]).toEqual({ proposalId: 'p1', success: true })
  })

  it('changed service: addService called AND supervisorctl restart issued', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, calls } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'p-changed',
        delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
      }),
    )

    expect(supervisord.addService).toHaveBeenCalledTimes(1)
    const restartCalls = calls.filter(
      (c) => c.command === 'supervisorctl' && c.args[0] === 'restart',
    )
    expect(restartCalls).toHaveLength(1)
    expect(restartCalls[0]?.args).toEqual(['restart', 'redis'])
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'p-changed', success: true })
  })

  it('restart failure is swallowed (best-effort) — overall apply still succeeds', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'supervisorctl' && args[0] === 'restart') {
        throw new Error('no such process')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'restart-fail',
        delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
      }),
    )

    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'restart-fail', success: true })
  })

  it('removed services: calls removeService per name, no addService, ack success', async () => {
    // Phase-2 reconciling policy (was Phase-1 warn-only): for each removed
    // service the applier MUST call supervisord.removeService so the
    // supervisord conf is stopped + removed from disk. Without this the
    // controller leaves stale `.conf` files on the persistent volume and
    // supervisord crash-loops on the next reread.
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, run } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'p-removed',
        delta: { removedServices: ['postgres', 'redis'], hasChanges: true },
      }),
    )

    expect(supervisord.addService).not.toHaveBeenCalled()
    // No supervisorctl calls go through the executor in the removal path —
    // they're all routed through SupervisordController.removeService (stubbed
    // here as a single fn). The executor itself remains untouched.
    expect(run).not.toHaveBeenCalled()
    expect(supervisord.removeService).toHaveBeenCalledTimes(2)
    expect(supervisord.removeService).toHaveBeenNthCalledWith(1, 'postgres')
    expect(supervisord.removeService).toHaveBeenNthCalledWith(2, 'redis')
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'p-removed', success: true })
  })

  it('removed services: removeService failure logs but does not fail the ack', async () => {
    // Best-effort policy on the removal path: a failure to tear down one
    // service (e.g. supervisorctl unavailable) must not turn the whole
    // delta into a failed ack. The additive changes above have already
    // landed; one stale conf left on disk is the lesser failure mode.
    const supervisord = makeSupervisord()
    supervisord.removeService.mockImplementation(async (_name: string) => {
      throw new Error('supervisorctl unavailable')
    })
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'p-removed-fails',
        delta: { removedServices: ['postgres'], hasChanges: true },
      }),
    )

    expect(supervisord.removeService).toHaveBeenCalledTimes(1)
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'p-removed-fails', success: true })
  })

  it('installChanged with new hash: runs install bash and persists hash', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, calls } = makeExecutor()
    const hashStore = makeHashStore() // fresh — cache is empty
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    const installScript = 'apt-get install -y postgresql-client'
    await applier.applyDelta(
      payload({
        proposalId: 'install-only',
        delta: {
          installChanged: true,
          installNew: installScript,
          hasChanges: true,
        },
      }),
    )

    expect(supervisord.addService).not.toHaveBeenCalled()
    // Install ran via bash -c with the mise-shims PATH.
    const bashCalls = calls.filter((c) => c.command === 'bash' && c.args[0] === '-c')
    expect(bashCalls).toHaveLength(1)
    expect(bashCalls[0]?.args).toEqual(['-c', installScript])
    expect(bashCalls[0]?.opts?.env?.['PATH']).toContain('/data/mise/shims')

    // Top-level hash now matches sha256(installScript); per-service map is
    // untouched (no services in delta).
    expect(hashStore.current().topLevel).toBe(sha256Hex(installScript))
    expect(hashStore.current().services).toEqual({})

    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'install-only', success: true })
  })

  it('installChanged but hash unchanged: skips install bash entirely', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, run } = makeExecutor()
    const installScript = 'apt-get install -y postgresql-client'
    // Seed the store with the SAME hash the delta would compute. Even though
    // the delta flags `installChanged: true`, the hash agrees so we trust
    // the hash and don't re-run.
    const hashStore = makeHashStore({
      initial: { topLevel: sha256Hex(installScript), services: {} },
    })
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'install-hash-match',
        delta: {
          installChanged: true,
          installNew: installScript,
          hasChanges: true,
        },
      }),
    )

    // No bash invocations — the hash gate held.
    expect(run).not.toHaveBeenCalled()
    expect(supervisord.addService).not.toHaveBeenCalled()
    expect(signalr.ackCalls()[0]).toEqual({
      proposalId: 'install-hash-match',
      success: true,
    })
  })

  it('top-level install failure: ack failure, hash NOT updated, services not processed', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const installScript = 'apt-get install -y broken-package'
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'bash' && args[0] === '-c' && args[1] === installScript) {
        throw new Error('apt-get failed')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const hashStore = makeHashStore() // fresh
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'install-fail',
        delta: {
          installChanged: true,
          installNew: installScript,
          // Service in same delta MUST NOT be processed once install fails.
          newOrChangedServices: [SVC_REDIS],
          hasChanges: true,
        },
      }),
    )

    expect(supervisord.addService).not.toHaveBeenCalled()
    // Hash unchanged — failed install does NOT poison the cache with its
    // (would-be) hash. The next apply will retry.
    expect(hashStore.current()).toEqual({ topLevel: '', services: {} })

    expect(signalr.ackCalls()[0]).toEqual({
      proposalId: 'install-fail',
      success: false,
      error: 'apt-get failed',
    })
  })

  it('per-service install runs BEFORE addService for that service', async () => {
    // We want to assert ordering: per-service install bash executes before
    // supervisord.addService is invoked for the same service. Capture the
    // order across both surfaces.
    const order: string[] = []
    const supervisord = makeSupervisord(async (svc) => {
      order.push(`addService:${svc.name}`)
    })
    const signalr = makeSignalR()
    const installScript = 'curl -L https://example.com/mongo > /usr/local/bin/mongod'
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'bash' && args[0] === '-c' && args[1] === installScript) {
        order.push('install:mongodb')
      }
      if (cmd === 'supervisorctl' && args[0] === 'restart') {
        order.push(`restart:${args[1]}`)
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const hashStore = makeHashStore() // fresh — service install runs
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    const SVC_MONGO: ServiceSpec = {
      name: 'mongodb',
      command: '/usr/local/bin/mongod',
      install: installScript,
    }

    await applier.applyDelta(
      payload({
        proposalId: 'svc-install',
        delta: { newOrChangedServices: [SVC_MONGO], hasChanges: true },
      }),
    )

    // Install MUST come before addService (otherwise supervisord crash-loops
    // on a missing binary).
    expect(order).toEqual(['install:mongodb', 'addService:mongodb', 'restart:mongodb'])
    // Per-service hash now matches.
    expect(hashStore.current().services['mongodb']).toBe(sha256Hex(installScript))
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'svc-install', success: true })
  })

  it('per-service install with unchanged hash: addService still runs, no install bash', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const installScript = 'echo redis-installed'
    const { executor, calls } = makeExecutor()
    // Seed the per-service hash so the install is gated as "already done".
    const hashStore = makeHashStore({
      initial: { topLevel: '', services: { redis: sha256Hex(installScript) } },
    })
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    const SVC_REDIS_INSTALL: ServiceSpec = {
      ...SVC_REDIS,
      install: installScript,
    }

    await applier.applyDelta(
      payload({
        proposalId: 'svc-hash-match',
        delta: { newOrChangedServices: [SVC_REDIS_INSTALL], hasChanges: true },
      }),
    )

    // No install bash invocation — hash matched.
    const bashCalls = calls.filter((c) => c.command === 'bash' && c.args[0] === '-c')
    expect(bashCalls).toHaveLength(0)
    // But supervisord.addService and the supervisorctl restart still ran —
    // the conf may have changed (command/user/env) even if the install
    // hasn't, so the service-side logic must still execute.
    expect(supervisord.addService).toHaveBeenCalledTimes(1)
    const restartCalls = calls.filter(
      (c) => c.command === 'supervisorctl' && c.args[0] === 'restart',
    )
    expect(restartCalls).toHaveLength(1)

    expect(signalr.ackCalls()[0]).toEqual({
      proposalId: 'svc-hash-match',
      success: true,
    })
  })

  it('per-service install failure: ack failure, addService NOT called for that service, hash NOT updated', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const installScript = 'curl https://example.com/broken'
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'bash' && args[0] === '-c' && args[1] === installScript) {
        throw new Error('curl failed: 404')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const hashStore = makeHashStore() // fresh
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    const SVC_BROKEN: ServiceSpec = {
      name: 'broken',
      command: '/usr/local/bin/broken',
      install: installScript,
    }

    await applier.applyDelta(
      payload({
        proposalId: 'svc-install-fail',
        delta: { newOrChangedServices: [SVC_BROKEN], hasChanges: true },
      }),
    )

    // The install threw → addService never reached.
    expect(supervisord.addService).not.toHaveBeenCalled()
    // Hash for that service NOT recorded.
    expect(hashStore.current().services).toEqual({})

    expect(signalr.ackCalls()[0]).toEqual({
      proposalId: 'svc-install-fail',
      success: false,
      error: 'curl failed: 404',
    })
  })

  it('top-level install + service install: top-level runs first, both hashes persisted', async () => {
    const order: string[] = []
    const supervisord = makeSupervisord(async (svc) => {
      order.push(`addService:${svc.name}`)
    })
    const signalr = makeSignalR()
    const topScript = 'mise install node@22'
    const svcScript = 'mkdir -p /data/mongo'
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'bash' && args[0] === '-c') {
        order.push(`bash:${args[1]}`)
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const hashStore = makeHashStore() // fresh
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
      hashStore: hashStore.store,
    })

    const SVC_MONGO: ServiceSpec = {
      name: 'mongodb',
      command: '/usr/local/bin/mongod',
      install: svcScript,
    }

    await applier.applyDelta(
      payload({
        proposalId: 'top-and-svc',
        delta: {
          installChanged: true,
          installNew: topScript,
          newOrChangedServices: [SVC_MONGO],
          hasChanges: true,
        },
      }),
    )

    // Top-level install first, then per-service install, then addService.
    expect(order).toEqual([`bash:${topScript}`, `bash:${svcScript}`, 'addService:mongodb'])

    // Both hashes recorded.
    const snap = hashStore.current()
    expect(snap.topLevel).toBe(sha256Hex(topScript))
    expect(snap.services['mongodb']).toBe(sha256Hex(svcScript))

    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'top-and-svc', success: true })
  })

  it('setupChanged: re-runs setup via bash -c with PATH including mise', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, calls } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'setup-rerun',
        delta: {
          setupChanged: true,
          setupNew: 'npm ci && npm run migrate',
          hasChanges: true,
        },
      }),
    )

    const bashCalls = calls.filter((c) => c.command === 'bash' && c.args[0] === '-c')
    expect(bashCalls).toHaveLength(1)
    expect(bashCalls[0]?.args).toEqual(['-c', 'npm ci && npm run migrate'])
    expect(bashCalls[0]?.opts?.env?.['PATH']).toContain('/data/mise/shims')
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'setup-rerun', success: true })
  })

  it('setupChanged but setupNew is empty/whitespace: no bash invocation', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, run } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'setup-empty',
        delta: {
          setupChanged: true,
          setupNew: '   \n\t  ',
          hasChanges: true,
        },
      }),
    )

    expect(run).not.toHaveBeenCalled()
    expect(signalr.ackCalls()[0]).toEqual({ proposalId: 'setup-empty', success: true })
  })

  it('setup failure → ack failure with the error message', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor } = makeExecutor(async (cmd, args) => {
      if (cmd === 'bash' && args[0] === '-c') {
        throw new Error('migration failed')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'setup-fail',
        delta: { setupChanged: true, setupNew: 'npm run migrate', hasChanges: true },
      }),
    )

    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(1)
    expect(acks[0]).toEqual({
      proposalId: 'setup-fail',
      success: false,
      error: 'migration failed',
    })
  })

  it('service failure on second item: ack failure with that error', async () => {
    let callIdx = 0
    const supervisord = makeSupervisord(async () => {
      callIdx += 1
      if (callIdx === 2) {
        throw new Error('supervisorctl reread failed')
      }
    })
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'p3',
        delta: {
          newOrChangedServices: [SVC_REDIS, SVC_POSTGRES],
          hasChanges: true,
        },
      }),
    )

    expect(supervisord.addService).toHaveBeenCalledTimes(2)
    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(1)
    expect(acks[0]).toEqual({
      proposalId: 'p3',
      success: false,
      error: 'supervisorctl reread failed',
    })
  })

  it('two concurrent applyDelta calls run serially via the chain', async () => {
    let resolveFirst: (() => void) | undefined
    const firstGate = new Promise<void>((r) => {
      resolveFirst = r
    })
    const order: string[] = []

    const supervisord = makeSupervisord(async (svc) => {
      order.push(`addService:${svc.name}:start`)
      if (svc.name === 'redis') {
        await firstGate
      }
      order.push(`addService:${svc.name}:end`)
    })
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    const first = applier.applyDelta(
      payload({
        proposalId: 'A',
        delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
      }),
    )
    const second = applier.applyDelta(
      payload({
        proposalId: 'B',
        delta: { newOrChangedServices: [SVC_POSTGRES], hasChanges: true },
      }),
    )

    // Pump microtasks until the first apply reaches its addService start.
    // The applier reads the hash store before the service loop, which adds
    // a couple of awaits — be tolerant rather than coupling the test to the
    // exact microtask count.
    for (let i = 0; i < 20 && order.length === 0; i += 1) {
      await Promise.resolve()
    }
    expect(order).toEqual(['addService:redis:start'])
    // And a few extra pumps to prove second is still gated.
    for (let i = 0; i < 5; i += 1) await Promise.resolve()
    expect(order).toEqual(['addService:redis:start'])

    resolveFirst!()
    await first
    await second

    expect(order).toEqual([
      'addService:redis:start',
      'addService:redis:end',
      'addService:postgres:start',
      'addService:postgres:end',
    ])

    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(2)
    expect(acks[0]?.proposalId).toBe('A')
    expect(acks[0]?.success).toBe(true)
    expect(acks[1]?.proposalId).toBe('B')
    expect(acks[1]?.success).toBe(true)
  })

  it('empty delta (no changes): ack success with no side effects', async () => {
    const supervisord = makeSupervisord()
    const signalr = makeSignalR()
    const { executor, run } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(payload({ proposalId: 'empty' }))

    expect(supervisord.addService).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(1)
    expect(acks[0]).toEqual({ proposalId: 'empty', success: true })
  })

  it('daemon stays up: applyDelta resolves even when supervisord throws', async () => {
    const supervisord = makeSupervisord(async () => {
      throw new Error('supervisord sync failure')
    })
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await expect(
      applier.applyDelta(
        payload({
          proposalId: 'p4',
          delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
        }),
      ),
    ).resolves.toBeUndefined()

    const acks = signalr.ackCalls()
    expect(acks[0]).toEqual({
      proposalId: 'p4',
      success: false,
      error: 'supervisord sync failure',
    })
  })

  it('a failed apply does not poison the chain — next apply runs', async () => {
    let firstCall = true
    const supervisord = makeSupervisord(async () => {
      if (firstCall) {
        firstCall = false
        throw new Error('first boom')
      }
    })
    const signalr = makeSignalR()
    const { executor } = makeExecutor()
    const applier = makeApplier({
      supervisord: supervisord.controller,
      signalr: signalr.client,
      executor,
    })

    await applier.applyDelta(
      payload({
        proposalId: 'fail',
        delta: { newOrChangedServices: [SVC_REDIS], hasChanges: true },
      }),
    )
    await applier.applyDelta(
      payload({
        proposalId: 'next',
        delta: { newOrChangedServices: [SVC_POSTGRES], hasChanges: true },
      }),
    )

    const acks = signalr.ackCalls()
    expect(acks).toHaveLength(2)
    expect(acks[0]?.success).toBe(false)
    expect(acks[1]?.success).toBe(true)
    expect(acks[1]?.proposalId).toBe('next')
  })
})
