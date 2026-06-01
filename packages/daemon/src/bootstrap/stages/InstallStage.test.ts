// Tests for InstallStage. Hand-rolled IExecutor + InstallHashStore fakes so
// nothing actually shells out or touches disk.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { ExecOpts, ExecResult, IExecutor } from '../../runtime/IExecutor.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'

import { BootstrapState } from '../BootstrapState.js'
import { InstallStage } from './InstallStage.js'
import {
  computeSpecHashes,
  type InstallHashStore,
  type InstallHashes,
} from '../../runtime/InstallHashStore.js'

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

interface PayloadOpts {
  install?: string
  installVerify?: string
  services?: Array<{
    name: string
    install?: string
    command?: string
    installVerify?: string
  }>
}

function payloadWithInstall(opts: PayloadOpts): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: {
      version: 2,
      ...(opts.install !== undefined ? { install: opts.install } : {}),
      ...(opts.installVerify !== undefined ? { installVerify: opts.installVerify } : {}),
      ...(opts.services !== undefined
        ? {
            services: opts.services.map((s) => ({
              name: s.name,
              command: s.command ?? `${s.name}-cmd`,
              ...(s.install !== undefined ? { install: s.install } : {}),
              ...(s.installVerify !== undefined ? { installVerify: s.installVerify } : {}),
            })),
          }
        : {}),
    },
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

interface FakeStore {
  store: InstallHashStore
  read: ReturnType<typeof vi.fn>
  write: ReturnType<typeof vi.fn>
  written: InstallHashes[]
}

function makeStore(initial?: InstallHashes, opts: { writeFails?: boolean; readThrows?: boolean } = {}): FakeStore {
  const written: InstallHashes[] = []
  const read = vi.fn(async (): Promise<InstallHashes> => {
    if (opts.readThrows === true) throw new Error('disk on fire')
    return initial ?? { topLevel: '', services: {} }
  })
  const write = vi.fn(async (h: InstallHashes): Promise<void> => {
    if (opts.writeFails === true) throw new Error('EACCES write')
    written.push({ topLevel: h.topLevel, services: { ...h.services } })
  })
  const store: Partial<InstallHashStore> = { read, write, get path() { return '/fake/install-hashes.json' } }
  return { store: store as InstallHashStore, read, write, written }
}

function reportingSignalr() {
  const events: Array<{ stage: string; status: string; detail?: string }> = []
  return {
    signalr: {
      reportBootstrapProgress: vi.fn(async (p: {
        stage: string
        status: 'started' | 'progress' | 'completed' | 'failed' | 'skipped'
        detail?: string
      }) => {
        events.push({ ...p })
      }),
    },
    events,
  }
}

describe('InstallStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('no-op when both top-level and per-service install are empty', async () => {
    const { signalr, events } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const { store, write } = makeStore()
    const state = new BootstrapState()
    state.setPayload(payloadWithInstall({ services: [{ name: 's1' }] }))

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).not.toHaveBeenCalled()
    expect(write).not.toHaveBeenCalled()
    expect(events.map((e) => e.status)).toContain('skipped')
  })

  it('skip path: stored hashes match current spec hashes → no exec', async () => {
    const { signalr, events } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'echo top',
      services: [{ name: 'mongo', install: 'echo mongo' }],
    })
    const cached = computeSpecHashes(payload.runtimeSpec)
    const { store, write } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).not.toHaveBeenCalled()
    expect(write).not.toHaveBeenCalled()
    expect(events.some((e) => e.status === 'skipped')).toBe(true)
  })

  it('run path: missing hash file → executes blob and persists new hashes', async () => {
    const { signalr, events } = reportingSignalr()
    const { executor, run, calls } = makeExecutor()
    // Default store returns empty hashes (first boot on fresh volume).
    const { store, write, written } = makeStore()
    const payload = payloadWithInstall({
      install: 'apt-get install -y curl',
      services: [{ name: 'mongo', install: 'echo install mongo' }],
    })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({
      signalr,
      state,
      executor,
      hashStore: store,
      now: () => 12345,
    })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).toHaveBeenCalledTimes(1)
    expect(calls[0]?.command).toBe('bash')
    expect(calls[0]?.args[0]).toBe('-c')
    // The wrapped script writes the blob to a tmp file and runs it via `bash`.
    const wrappedCmd = String(calls[0]?.args[1])
    expect(wrappedCmd).toContain('apt-get install -y curl')
    expect(wrappedCmd).toContain('echo install mongo')

    // Persisted hashes match what we'd compute now.
    expect(write).toHaveBeenCalledTimes(1)
    expect(written[0]).toEqual(computeSpecHashes(payload.runtimeSpec))

    expect(events.map((e) => e.status)).toEqual(
      expect.arrayContaining(['started', 'completed']),
    )
  })

  it('run path: cached hashes differ → re-executes blob', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const { store, write, written } = makeStore({
      topLevel: 'stale-top',
      services: { mongo: 'stale-mongo' },
    })
    const payload = payloadWithInstall({
      install: 'echo new top',
      services: [{ name: 'mongo', install: 'echo new mongo' }],
    })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).toHaveBeenCalledTimes(1)
    expect(write).toHaveBeenCalledTimes(1)
    const desired = computeSpecHashes(payload.runtimeSpec)
    expect(written[0]).toEqual(desired)
  })

  it('per-service hash tracking: changing one service\'s install re-triggers a run', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    // Old cache: matches top-level + service A, but service B has changed.
    const payload = payloadWithInstall({
      install: 'echo top',
      services: [
        { name: 'a', install: 'echo a-v1' },
        { name: 'b', install: 'echo b-v2' }, // changed
      ],
    })
    const desired = computeSpecHashes(payload.runtimeSpec)
    const cached: InstallHashes = {
      topLevel: desired.topLevel,
      services: {
        a: desired.services['a']!,
        b: 'old-b-hash', // outdated
      },
    }
    const { store, written } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).toHaveBeenCalledTimes(1)
    expect(written[0]).toEqual(desired)
  })

  it('per-service hash tracking: new service in spec → re-run', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'echo top',
      services: [
        { name: 'a', install: 'echo a' },
        { name: 'newcomer', install: 'echo newcomer' },
      ],
    })
    const cached: InstallHashes = {
      topLevel: computeSpecHashes(payload.runtimeSpec).topLevel,
      services: { a: computeSpecHashes(payload.runtimeSpec).services['a']! },
    }
    const { store } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    await stage.run(makeContext())
    expect(run).toHaveBeenCalledTimes(1)
  })

  it('per-service hash tracking: removed service → re-run', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'echo top',
      services: [{ name: 'a', install: 'echo a' }],
    })
    const cached: InstallHashes = {
      topLevel: computeSpecHashes(payload.runtimeSpec).topLevel,
      services: {
        a: computeSpecHashes(payload.runtimeSpec).services['a']!,
        ghost: 'whoops-still-here',
      },
    }
    const { store } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    await stage.run(makeContext())
    expect(run).toHaveBeenCalledTimes(1)
  })

  it('non-zero exit fails the stage as recoverable', async () => {
    const { signalr, events } = reportingSignalr()
    const { executor } = makeExecutor(async () => {
      throw new Error('bash failed (exit 127): mongod: command not found')
    })
    const { store, write } = makeStore()
    const payload = payloadWithInstall({ install: 'mongod --do-the-thing' })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/mongod: command not found/)
    }

    // We did NOT persist hashes — the install failed, the cache must NOT
    // claim the script as up-to-date.
    expect(write).not.toHaveBeenCalled()
    expect(events.some((e) => e.status === 'failed')).toBe(true)
  })

  it('atomic-write-of-hash-file: persist failure does NOT fail the stage', async () => {
    const { signalr } = reportingSignalr()
    const { executor } = makeExecutor()
    const { store } = makeStore({ topLevel: '', services: {} }, { writeFails: true })
    const payload = payloadWithInstall({ install: 'echo something' })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    // Install ran fine — the cost is just an extra re-run next boot.
    expect(result).toEqual({ ok: true })
  })

  it('store read throwing is treated as empty cache (re-run)', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const { store, written } = makeStore(undefined, { readThrows: true })
    const payload = payloadWithInstall({ install: 'echo something' })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    await stage.run(makeContext())
    expect(run).toHaveBeenCalledTimes(1)
    expect(written[0]).toEqual(computeSpecHashes(payload.runtimeSpec))
  })

  it('pre-aborted signal returns recoverable failure without running anything', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const { store, read } = makeStore()
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    state.setPayload(payloadWithInstall({ install: 'echo hi' }))

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(run).not.toHaveBeenCalled()
    expect(read).not.toHaveBeenCalled()
  })

  it('PATH includes mise shims so install-time mise commands work', async () => {
    const { signalr } = reportingSignalr()
    const { executor, calls } = makeExecutor()
    const { store } = makeStore()
    const state = new BootstrapState()
    state.setPayload(payloadWithInstall({ install: 'mise install node@22' }))

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    await stage.run(makeContext())
    expect(calls[0]?.opts?.env?.['PATH']).toContain('/data/mise/shims')
  })

  // ── installVerify: belt-and-suspenders for host-migration rootfs wipes ──
  //
  // When the hash matches we'd normally skip. But if the user supplied an
  // installVerify predicate (e.g. `command -v mongod`), we run it first;
  // a non-zero exit means the rootfs was wiped beneath us (host migration)
  // and we must re-install even though the hash store on /data says we
  // already did.

  it('verify passes (exit 0) on hash match → skip is honoured', async () => {
    const { signalr, events } = reportingSignalr()
    // First call (verify) exits 0; no further calls should happen.
    const { executor, run, calls } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'apt-get install -y mongodb-org',
      installVerify: 'command -v mongod',
      services: [{ name: 'mongo', install: 'echo init', installVerify: 'command -v mongod' }],
    })
    const cached = computeSpecHashes(payload.runtimeSpec)
    const { store, write } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    // Two verify calls (top-level + service:mongo), no install bash, no
    // hash write — verify was happy, skip stands.
    expect(run).toHaveBeenCalledTimes(2)
    expect(calls[0]?.args[1]).toBe('command -v mongod')
    expect(calls[1]?.args[1]).toBe('command -v mongod')
    expect(write).not.toHaveBeenCalled()
    expect(events.some((e) => e.status === 'skipped')).toBe(true)
  })

  it('top-level verify fails (non-zero) on hash match → re-runs install', async () => {
    const { signalr, events } = reportingSignalr()
    // First call is the verify (fails). Second call must be the install bash
    // re-running. We script the executor to fail only the first call.
    let callIdx = 0
    const { executor, run, calls } = makeExecutor(async (_cmd, args) => {
      callIdx += 1
      // Verify uses `command -v …`; install uses the wrapped heredoc.
      if (callIdx === 1 && String(args[1]).startsWith('command -v')) {
        throw new Error('bash failed (exit 1): mongod: command not found')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const payload = payloadWithInstall({
      install: 'apt-get install -y mongodb-org',
      installVerify: 'command -v mongod',
    })
    const cached = computeSpecHashes(payload.runtimeSpec)
    const { store, write, written } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    // 1× verify (failed) + 1× install bash (succeeded) = 2 executor calls.
    expect(run).toHaveBeenCalledTimes(2)
    expect(calls[0]?.args[1]).toBe('command -v mongod')
    expect(String(calls[1]?.args[1])).toContain('apt-get install -y mongodb-org')
    // Hash gets persisted again (defensive — proves the install branch ran).
    expect(write).toHaveBeenCalledTimes(1)
    expect(written[0]).toEqual(cached)
    // Telemetry shows the verify-failure progress event before the install.
    expect(events.some((e) => e.status === 'progress' && e.detail?.includes('verify failed'))).toBe(true)
  })

  it('per-service verify fails → re-runs install (top-level verify ok)', async () => {
    const { signalr } = reportingSignalr()
    let callIdx = 0
    const { executor, run, calls } = makeExecutor(async (_cmd, args) => {
      callIdx += 1
      const cmd = String(args[1])
      // Call 1: top-level verify — succeeds.
      if (callIdx === 1 && cmd === 'command -v top-binary') {
        return { stdout: '', stderr: '', exitCode: 0 }
      }
      // Call 2: per-service verify — fails (binary missing).
      if (callIdx === 2 && cmd === 'command -v mariadbd') {
        throw new Error('bash failed (exit 1): mariadbd: command not found')
      }
      // Call 3: install bash — succeeds.
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const payload = payloadWithInstall({
      install: 'apt-get install -y top-binary',
      installVerify: 'command -v top-binary',
      services: [
        {
          name: 'mariadb',
          install: 'apt-get install -y mariadb-server',
          installVerify: 'command -v mariadbd',
        },
      ],
    })
    const cached = computeSpecHashes(payload.runtimeSpec)
    const { store } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(run).toHaveBeenCalledTimes(3)
    // Top-level verify ran first, per-service second, install third.
    expect(calls[0]?.args[1]).toBe('command -v top-binary')
    expect(calls[1]?.args[1]).toBe('command -v mariadbd')
    expect(String(calls[2]?.args[1])).toContain('apt-get install -y top-binary')
    expect(String(calls[2]?.args[1])).toContain('apt-get install -y mariadb-server')
  })

  it('hash mismatch path: verify is NOT consulted (install runs anyway)', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run, calls } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'echo top',
      installVerify: 'command -v top-binary', // should be ignored — hash mismatch
    })
    // Stale cache → hash mismatch → straight to install path.
    const { store } = makeStore({ topLevel: 'stale', services: {} })
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    await stage.run(makeContext())

    // Only the install bash ran — verify is the skip-path gate and is
    // bypassed when we're going to install anyway.
    expect(run).toHaveBeenCalledTimes(1)
    expect(String(calls[0]?.args[1])).toContain('echo top')
  })

  it('no verify configured on skip path → behaves as before (no verify call)', async () => {
    const { signalr } = reportingSignalr()
    const { executor, run } = makeExecutor()
    const payload = payloadWithInstall({
      install: 'echo top',
      services: [{ name: 's1', install: 'echo s1' }],
    })
    const cached = computeSpecHashes(payload.runtimeSpec)
    const { store } = makeStore(cached)
    const state = new BootstrapState()
    state.setPayload(payload)

    const stage = new InstallStage({ signalr, state, executor, hashStore: store })
    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    // No installVerify on either scope → no verify calls, classic skip.
    expect(run).not.toHaveBeenCalled()
  })

  it('uses configured timeout', async () => {
    const { signalr } = reportingSignalr()
    const { executor, calls } = makeExecutor()
    const { store } = makeStore()
    const state = new BootstrapState()
    state.setPayload(payloadWithInstall({ install: 'echo hi' }))

    const stage = new InstallStage({
      signalr,
      state,
      executor,
      hashStore: store,
      timeoutMs: 1234,
    })
    await stage.run(makeContext())
    expect(calls[0]?.opts?.timeoutMs).toBe(1234)
  })
})
