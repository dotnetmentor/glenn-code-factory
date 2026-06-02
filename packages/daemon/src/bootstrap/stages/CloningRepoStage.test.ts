// Tests for CloningRepoStage. Hand-rolled fakes for fs (mkdir / access),
// IExecutor, and TokenManager — never spawn `git`, never touch disk, never
// log the token.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { ExecOpts, ExecResult, IExecutor } from '../../runtime/IExecutor.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'
import type { TokenManager } from '../../github/TokenManager.js'

import { BootstrapState } from '../BootstrapState.js'
import { CloningRepoStage, type CloningRepoStageFs } from './CloningRepoStage.js'

const FAKE_TOKEN = 'ghs_FAKE_INSTALL_TOKEN_xyz'

// What our Basic-auth header should look like for the fake token. GitHub's
// git HTTP backend wants `x-access-token:<token>` as the credential pair,
// base64-encoded into the value half of `Authorization: Basic …`. Mirrors
// `encodeBasicAuth` in CloningRepoStage.ts — duplicated rather than imported
// so the test would catch any accidental shape change there.
const FAKE_BASIC_AUTH = Buffer.from(`x-access-token:${FAKE_TOKEN}`, 'utf8').toString('base64')

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

function makeContext(opts: { signal?: AbortSignal; logger?: Logger } = {}): BootstrapContext {
  return {
    config: {} as DaemonConfig,
    signalr: {} as SignalRClient,
    logger: opts.logger ?? (makeLogger() as unknown as Logger),
    signal: opts.signal ?? new AbortController().signal,
  }
}

function payloadWithRepo(): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: { version: 2 },
    envVars: [],
    hooks: null,
    mcps: [],
    repo: {
      url: 'https://github.com/glenn/proj.git',
      branch: 'main',
      // deployKey is now optional/nullable; not used by the HTTPS path.
    },
  }
}

function makeFs(opts: { gitDirExists?: boolean; failOn?: 'mkdir' } = {}): CloningRepoStageFs {
  return {
    mkdir: vi.fn(async () => {
      if (opts.failOn === 'mkdir') throw new Error('ENOSPC mkdir')
      return undefined
    }) as unknown as CloningRepoStageFs['mkdir'],
    access: vi.fn(async () => {
      if (opts.gitDirExists !== true) throw new Error('ENOENT')
    }) as unknown as CloningRepoStageFs['access'],
  }
}

function makeExecutor(impl?: (cmd: string, args: readonly string[]) => Promise<ExecResult>) {
  const calls: Array<{ command: string; args: readonly string[]; opts?: ExecOpts }> = []
  const run = vi.fn(
    async (command: string, args: readonly string[], opts?: ExecOpts): Promise<ExecResult> => {
      calls.push({ command, args, ...(opts !== undefined ? { opts } : {}) })
      if (impl) {
        const result = await impl(command, args)
        return result
      }
      // Default: HEAD already on `main` so the branch-reconcile short-circuits
      // and existing happy-path tests see the original git argv shape.
      if (
        command === 'git' &&
        args.includes('rev-parse') &&
        args.includes('--abbrev-ref') &&
        args.includes('HEAD')
      ) {
        return { stdout: 'main\n', stderr: '', exitCode: 0 }
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    },
  )
  const executor: IExecutor = { run }
  return { executor, run, calls }
}

function makeTokenManager(token: string = FAKE_TOKEN) {
  const getToken = vi.fn(async (_repo: string, _opts?: { forceRefresh?: boolean }) => token)
  const invalidate = vi.fn<(repo: string) => void>()
  const tokenManager: TokenManager = { getToken, invalidate }
  return { tokenManager, getToken, invalidate }
}

describe('CloningRepoStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('skipped when payload.repo is null', async () => {
    const fs = makeFs()
    const { executor, run } = makeExecutor()
    const { tokenManager, getToken } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload({ ...payloadWithRepo(), repo: null })
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(run).not.toHaveBeenCalled()
    expect(getToken).not.toHaveBeenCalled()
  })

  it('happy path: fresh clone — fetches token and uses http.extraHeader basic auth', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor, calls } = makeExecutor()
    const { tokenManager, getToken } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(getToken).toHaveBeenCalledWith('glenn/proj')

    const gitCalls = calls.filter((c) => c.command === 'git')
    // 1 clone + 1 rev-parse (branch reconcile, short-circuits since HEAD === main).
    expect(gitCalls).toHaveLength(2)
    expect(gitCalls[0]?.args).toEqual([
      '-c',
      `http.extraHeader=Authorization: Basic ${FAKE_BASIC_AUTH}`,
      'clone',
      '--depth=1',
      '--branch',
      'main',
      'https://github.com/glenn/proj.git',
      '/data/project/repo',
    ])
    // The follow-up call is the branch-reconcile rev-parse, NOT another
    // remote operation — confirms the short-circuit fired.
    expect(gitCalls[1]?.args).toEqual([
      '-C',
      '/data/project/repo',
      'rev-parse',
      '--abbrev-ref',
      'HEAD',
    ])
    // No GIT_SSH_COMMAND in env any more.
    expect(gitCalls[0]?.opts?.env?.['GIT_SSH_COMMAND']).toBeUndefined()
    // GIT_TERMINAL_PROMPT must be disabled so an auth failure surfaces
    // as a real 401 rather than the cryptic "could not read Username".
    expect(gitCalls[0]?.opts?.env?.['GIT_TERMINAL_PROMPT']).toBe('0')
  })

  it('warm volume on expected branch: no fetch/reset (Fly volume is source of truth)', async () => {
    // Per the daemon-git-sync-redesign: once a runtime exists the volume is
    // the source of truth — a warm boot already on the expected branch does
    // NOT pull from origin. (The first session's `pullBeforeStart` is where
    // origin catches up, handled in TurnRunner, not here.) The default
    // executor reports HEAD on `main`, so both the warm-path branch check and
    // the reconcile check short-circuit: the only git ops are the two
    // `rev-parse` reads — no fetch, no reset, no reset --hard, no token.
    const fs = makeFs({ gitDirExists: true })
    const { executor, calls } = makeExecutor()
    const { tokenManager, getToken } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    const gitCalls = calls.filter((c) => c.command === 'git')
    // Two rev-parse reads only: warm-path branch check + reconcile check.
    expect(gitCalls).toHaveLength(2)
    expect(gitCalls.every((c) => c.args.includes('rev-parse'))).toBe(true)
    expect(gitCalls[0]?.args).toEqual([
      '-C',
      '/data/project/repo',
      'rev-parse',
      '--abbrev-ref',
      'HEAD',
    ])
    expect(gitCalls[1]?.args).toEqual([
      '-C',
      '/data/project/repo',
      'rev-parse',
      '--abbrev-ref',
      'HEAD',
    ])
    // No remote op ⇒ no installation token ever requested.
    expect(getToken).not.toHaveBeenCalled()
  })

  it('git failure (non-auth) returns recoverable failure without retry', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor, run } = makeExecutor(async () => {
      throw new Error('fatal: unable to access remote (network unreachable)')
    })
    const { tokenManager, getToken, invalidate } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/network unreachable/)
    }
    // No retry — only one fetch + one git call.
    expect(getToken).toHaveBeenCalledTimes(1)
    expect(invalidate).not.toHaveBeenCalled()
    expect(run).toHaveBeenCalledTimes(1)
  })

  it('auth-failure on first attempt → invalidates + retries with forceRefresh; succeeds second time', async () => {
    const fs = makeFs({ gitDirExists: false })
    let attempt = 0
    const { executor, run } = makeExecutor(async (_cmd, args) => {
      // Branch-reconcile rev-parse is local and not part of the auth retry
      // dance — return HEAD === main so it short-circuits.
      if (args.includes('rev-parse')) return { stdout: 'main\n', stderr: '', exitCode: 0 }
      attempt += 1
      if (attempt === 1) {
        throw new Error('fatal: Authentication failed for https://github.com/glenn/proj.git/')
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const { tokenManager, getToken, invalidate } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })
    expect(invalidate).toHaveBeenCalledWith('glenn/proj')
    expect(getToken).toHaveBeenCalledTimes(2)
    // Second call had forceRefresh: true.
    expect(getToken).toHaveBeenNthCalledWith(2, 'glenn/proj', { forceRefresh: true })
    // 1 failed clone + 1 retried clone + 1 rev-parse (branch reconcile).
    expect(run).toHaveBeenCalledTimes(3)
  })

  it('auth-failure on both attempts → recoverable failure surfaced', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor } = makeExecutor(async () => {
      throw new Error('remote: 401 Unauthorized')
    })
    const { tokenManager, invalidate } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/401/i)
    }
    expect(invalidate).toHaveBeenCalledWith('glenn/proj')
  })

  it('pre-aborted signal returns recoverable failure without touching tokenManager', async () => {
    const fs = makeFs()
    const { executor, run } = makeExecutor()
    const { tokenManager, getToken } = makeTokenManager()
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(run).not.toHaveBeenCalled()
    expect(getToken).not.toHaveBeenCalled()
  })

  it('rejects unparseable repo URLs as a recoverable failure', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor, run } = makeExecutor()
    const { tokenManager, getToken } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload({
      ...payloadWithRepo(),
      repo: { url: 'git@github.com:glenn/proj.git', branch: 'main' },
    })
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/repo url parse failed/)
    }
    expect(getToken).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })

  it('never logs the token contents anywhere (logs or progress events)', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor } = makeExecutor()
    const logger = makeLogger()
    const reportBootstrapProgress = vi.fn(async (_p: unknown) => {})
    const { tokenManager } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress },
      state,
      executor,
      fs,
      tokenManager,
    })

    await stage.run(makeContext({ logger: logger as unknown as Logger }))

    const allLoggedJson = JSON.stringify([
      ...logger.info.mock.calls,
      ...logger.debug.mock.calls,
      ...logger.warn.mock.calls,
      ...logger.error.mock.calls,
      ...reportBootstrapProgress.mock.calls,
    ])
    expect(allLoggedJson).not.toContain(FAKE_TOKEN)
  })

  // === Branch reconciliation (copy-branch feature) ===
  //
  // A volume forked from another runtime arrives with `.git` populated and
  // HEAD on the SOURCE branch. The stage must detect this and switch HEAD
  // to the target branch. These tests cover the new logic.

  it('cloned volume: HEAD on source branch → fetches + switches to target', async () => {
    const fs = makeFs({ gitDirExists: true })
    // First rev-parse returns source branch (the "cloned volume" state),
    // post-switch rev-parse returns the target. Status returns clean tree.
    let revParseCalls = 0
    const { executor, calls } = makeExecutor(async (_cmd, args) => {
      if (args.includes('rev-parse')) {
        revParseCalls += 1
        if (revParseCalls === 1) return { stdout: 'payments\n', stderr: '', exitCode: 0 }
        return { stdout: 'payments-copy\n', stderr: '', exitCode: 0 }
      }
      if (args.includes('status')) return { stdout: '', stderr: '', exitCode: 0 }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const { tokenManager } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload({
      ...payloadWithRepo(),
      repo: { url: 'https://github.com/glenn/proj.git', branch: 'payments-copy' },
    })
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    const gitCalls = calls.filter((c) => c.command === 'git')
    // New warm-volume CopyBranch path: HEAD is on the source branch, so the
    // warm block does a non-destructive fetch + `checkout -B <target>`. The
    // subsequent reconcile read then sees HEAD already on the target and
    // short-circuits. Four git ops total:
    //   0: rev-parse (warm-path branch check — sees source ⇒ mismatch)
    //   1: fetch origin +refs/heads/<target> (with basic auth)
    //   2: checkout -B <target> origin/<target>
    //   3: rev-parse (reconcile check — sees target ⇒ short-circuit)
    expect(gitCalls).toHaveLength(4)

    // The warm-path fetch (call index 1) targets the destination branch with
    // basic-auth, non-destructively (no reset --hard — CopyBranch pushed the
    // destination from the source tip, so origin/<target> == the volume's HEAD).
    expect(gitCalls[1]?.args).toEqual([
      '-c',
      `http.extraHeader=Authorization: Basic ${FAKE_BASIC_AUTH}`,
      '-C',
      '/data/project/repo',
      'fetch',
      '--depth=1',
      'origin',
      '+refs/heads/payments-copy:refs/remotes/origin/payments-copy',
    ])
    // The switch (call index 2). Uses `checkout -B` to force-create a local
    // branch tracking the just-fetched `origin/<target>` ref — needed because
    // `git checkout <branch>` does NOT reliably auto-guess from the remote in
    // a shallow single-branch clone whose origin config is locked to a
    // different branch (the forked-volume case).
    expect(gitCalls[2]?.args).toEqual([
      '-C',
      '/data/project/repo',
      'checkout',
      '-B',
      'payments-copy',
      'origin/payments-copy',
    ])
    // Local switch needs no token; the only auth'd op is the fetch above.
    expect(gitCalls[2]?.opts?.env?.['GIT_TERMINAL_PROMPT']).toBe('0')
  })

  it('cloned volume: switch failed to land on target → recoverable failure', async () => {
    const fs = makeFs({ gitDirExists: true })
    // Both rev-parse calls return the source branch — switch didn't take.
    const { executor } = makeExecutor(async (_cmd, args) => {
      if (args.includes('rev-parse')) return { stdout: 'payments\n', stderr: '', exitCode: 0 }
      if (args.includes('status')) return { stdout: '', stderr: '', exitCode: 0 }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const { tokenManager } = makeTokenManager()
    const state = new BootstrapState()
    state.setPayload({
      ...payloadWithRepo(),
      repo: { url: 'https://github.com/glenn/proj.git', branch: 'payments-copy' },
    })
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/branch switch failed/)
      expect(result.reason).toMatch(/payments-copy/)
    }
  })

  it('cloned volume: dirty tree after reconcile switch → succeeds with warning, does NOT fail', async () => {
    const fs = makeFs({ gitDirExists: true })
    // The post-switch dirty-tree warning lives in the reconcile safety-net
    // (the warm-path checkout itself does no status check). To exercise it we
    // model the case where the warm-path `checkout -B` did NOT stick: the
    // reconcile read still sees the source branch, so reconcile runs its own
    // fetch + `switch -C`, which DOES land on the target — and the resulting
    // working tree is unexpectedly dirty. rev-parse sequence:
    //   1: warm-path read       → source  (mismatch ⇒ warm fetch+checkout)
    //   2: reconcile read       → source  (still mismatch ⇒ reconcile switches)
    //   3: reconcile verify read → target (switch landed ⇒ status check runs)
    let revParseCalls = 0
    const { executor } = makeExecutor(async (_cmd, args) => {
      if (args.includes('rev-parse')) {
        revParseCalls += 1
        return revParseCalls <= 2
          ? { stdout: 'payments\n', stderr: '', exitCode: 0 }
          : { stdout: 'payments-copy\n', stderr: '', exitCode: 0 }
      }
      if (args.includes('status')) {
        return { stdout: ' M src/foo.ts\n', stderr: '', exitCode: 0 }
      }
      return { stdout: '', stderr: '', exitCode: 0 }
    })
    const { tokenManager } = makeTokenManager()
    const logger = makeLogger()
    const state = new BootstrapState()
    state.setPayload({
      ...payloadWithRepo(),
      repo: { url: 'https://github.com/glenn/proj.git', branch: 'payments-copy' },
    })
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      executor,
      fs,
      tokenManager,
      repoDir: '/data/project/repo',
    })

    const result = await stage.run(makeContext({ logger: logger as unknown as Logger }))
    expect(result).toEqual({ ok: true })
    // Warning logged but stage still succeeded.
    expect(logger.warn).toHaveBeenCalled()
  })

  it('progress stream strips lines containing Authorization: header (defence in depth)', async () => {
    const fs = makeFs({ gitDirExists: false })
    const { executor } = makeExecutor()
    // Hook the executor to feed a synthesised stderr chunk that contains a
    // bogus Authorization line — verifies the sanitiser.
    const reportBootstrapProgress = vi.fn(async (_p: unknown) => {})
    const { tokenManager } = makeTokenManager()

    // Replace executor.run so we can drive onStderr ourselves.
    const run = vi.fn(
      async (
        _cmd: string,
        args: readonly string[],
        opts?: ExecOpts,
      ): Promise<ExecResult> => {
        // The branch-reconcile rev-parse must report HEAD on `main` so we
        // short-circuit the switch — we're only testing the sanitiser here.
        if (args.includes('rev-parse')) {
          return { stdout: 'main\n', stderr: '', exitCode: 0 }
        }
        opts?.onStderr?.('Cloning into /data/project/repo...\nAuthorization: Bearer leak-me\nDone.')
        return { stdout: '', stderr: '', exitCode: 0 }
      },
    )
    const customExecutor: IExecutor = { run }

    const state = new BootstrapState()
    state.setPayload(payloadWithRepo())
    const stage = new CloningRepoStage({
      signalr: { reportBootstrapProgress },
      state,
      executor: customExecutor,
      fs,
      tokenManager,
    })

    await stage.run(makeContext())

    const allProgress = JSON.stringify(reportBootstrapProgress.mock.calls)
    expect(allProgress).not.toContain('Authorization')
    expect(allProgress).not.toContain('leak-me')
    // The non-authorization lines DID make it through.
    expect(allProgress).toContain('Cloning into')
  })
})
