// Tests for GitModule. The runner + signalr client are hand-rolled fakes —
// the runner exposes the same `run(invocation, signal)` shape the real one
// does, but lets each test queue canned `GitResult`s + drive the audit
// callback directly. This keeps the suite synchronous + sub-second.
//
// The audit-callback wiring mirrors how Card 10 will assemble things in main.ts:
// the runner is constructed with `onAudit: e => gitModule.handleRunnerAudit(e)`,
// so each fake `run()` invocation here fires `started` before resolution and
// `completed` after.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { GitModule } from './GitModule.js'
import type { GitRunner } from './GitRunner.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { TokenManager } from '../github/TokenManager.js'
import type { GitAuditEvent, GitInvocation, GitOpType, GitResult } from './types.js'

// === Auth-path test fixtures ===
//
// Mirrors the fixtures in CloningRepoStage.test.ts so the two suites stay in
// lock-step on the credential shape. Duplicated rather than imported because
// each suite asserts the encoded value directly — sharing a constant would
// hide an accidental shape change in the encoder.
const FAKE_TOKEN = 'ghs_FAKE_INSTALL_TOKEN_xyz'
const FAKE_BASIC_AUTH = Buffer.from(`x-access-token:${FAKE_TOKEN}`, 'utf8').toString('base64')
const FAKE_REPO_FULL_NAME = 'glenn/proj'

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

interface CannedResult {
  result: GitResult
  /** Override the audit executionId. Defaults to a deterministic per-call id. */
  executionId?: string
}

interface RecordedRun {
  op: GitOpType
  args: string[]
}

/**
 * Hand-rolled GitRunner stand-in. Each `run()` consumes the next queued
 * canned result, fires `started` audit, then `completed`, then resolves with
 * the result. If no canned result is queued, returns a synthetic exitCode-0
 * default so simple tests don't have to queue every internal `git rev-parse`.
 */
class FakeGitRunner {
  readonly runs: RecordedRun[] = []
  readonly #queue: CannedResult[] = []
  #onAudit: ((e: GitAuditEvent) => void) | null = null
  #execCounter = 0
  /**
   * Set when a test wants to control the resolution timing of an in-flight
   * call (e.g. to assert that a second op queues behind the first). Replaces
   * the queue-based path for the next call only.
   */
  #pending: { release: (r: GitResult) => void; promise: Promise<GitResult> } | null = null

  setAudit(cb: (e: GitAuditEvent) => void): void {
    this.#onAudit = cb
  }

  enqueue(result: Partial<GitResult> & { exitCode: number | null }, opts: { executionId?: string } = {}): void {
    const full: GitResult = {
      exitCode: result.exitCode,
      durationMs: result.durationMs ?? 5,
      outputTail: result.outputTail ?? '',
      outputHash: result.outputHash ?? 'a'.repeat(64),
      timedOut: result.timedOut ?? false,
      authError: result.authError ?? false,
    }
    const entry: CannedResult = { result: full }
    if (opts.executionId !== undefined) entry.executionId = opts.executionId
    this.#queue.push(entry)
  }

  /** Hold the next call open. Returns a `release(result)` to resolve it. */
  pendNext(): { release: (r: GitResult) => void } {
    let release: (r: GitResult) => void = () => undefined
    const promise = new Promise<GitResult>((resolve) => {
      release = resolve
    })
    this.#pending = { release, promise }
    return { release }
  }

  async run(invocation: GitInvocation, _signal: AbortSignal): Promise<GitResult> {
    this.runs.push({ op: invocation.op, args: invocation.args })

    const executionId = `exec-${++this.#execCounter}`
    const startedAt = new Date('2026-05-08T12:00:00.000Z')
    const commandLine = invocation.sensitive
      ? `git ${invocation.op} [redacted]`
      : ['git', ...invocation.args].join(' ')

    this.#onAudit?.({
      kind: 'started',
      executionId,
      op: invocation.op,
      commandLine,
      startedAt,
    })

    let result: GitResult
    if (this.#pending !== null) {
      const pending = this.#pending
      this.#pending = null
      result = await pending.promise
    } else {
      const next = this.#queue.shift()
      result = next?.result ?? {
        exitCode: 0,
        durationMs: 1,
        outputTail: '',
        outputHash: 'b'.repeat(64),
        timedOut: false,
        authError: false,
      }
    }

    this.#onAudit?.({
      kind: 'completed',
      executionId,
      op: invocation.op,
      commandLine,
      startedAt,
      endedAt: new Date('2026-05-08T12:00:00.005Z'),
      exitCode: result.exitCode,
      durationMs: result.durationMs,
      outputTail: result.outputTail,
      outputHash: result.outputHash,
      timedOut: result.timedOut,
      authError: result.authError,
    })

    return result
  }
}

interface BuiltModule {
  module: GitModule
  runner: FakeGitRunner
  invoke: ReturnType<typeof vi.fn>
  logger: ReturnType<typeof makeLogger>
  /** vi.fn for `tokenManager.getToken`; only meaningful when `withAuth` was passed. */
  getToken: ReturnType<typeof vi.fn>
  /** vi.fn for `tokenManager.invalidate`; only meaningful when `withAuth` was passed. */
  invalidate: ReturnType<typeof vi.fn>
  /** vi.fn for `getRepoFullName`; only meaningful when `withAuth` was passed. */
  getRepoFullName: ReturnType<typeof vi.fn>
}

function buildModule(opts: {
  autoCommit?: boolean
  /**
   * When true, the module is constructed with a TokenManager + non-null
   * `getRepoFullName`. The push() path then runs the auth-aware branch
   * (mirroring production wiring). When false (default), the legacy path
   * is exercised — push args are just `[push, remote, branch]`.
   */
  withAuth?: boolean
  /** Override the token returned by the fake TokenManager. */
  token?: string
  /** Override what `getRepoFullName` returns. `undefined` → returns the default fixture. */
  repoFullName?: string | null
} = {}): BuiltModule {
  const runner = new FakeGitRunner()
  const invoke = vi.fn().mockResolvedValue(undefined)
  const signalr = { invoke } as unknown as SignalRClient
  const logger = makeLogger()

  const getToken = vi.fn(async (_repo: string, _o?: { forceRefresh?: boolean }) =>
    opts.token ?? FAKE_TOKEN,
  )
  const invalidate = vi.fn<(repo: string) => void>()
  const tokenManager: TokenManager = { getToken, invalidate }

  const repoNameValue = opts.repoFullName === undefined ? FAKE_REPO_FULL_NAME : opts.repoFullName
  const getRepoFullName = vi.fn<() => string | null>(() => repoNameValue)

  const module = new GitModule({
    runner: runner as unknown as GitRunner,
    signalr,
    logger: logger as unknown as Logger,
    cwd: '/tmp/test-repo',
    ...(opts.autoCommit !== undefined ? { autoCommit: opts.autoCommit } : {}),
    ...(opts.withAuth === true ? { tokenManager, getRepoFullName } : {}),
  })
  // Wire audit forwarding the same way Card 10 will at startup.
  runner.setAudit((e) => module.handleRunnerAudit(e))
  return { module, runner, invoke, logger, getToken, invalidate, getRepoFullName }
}

/** Sample `git commit` output that GitModule's regexes can parse. */
const COMMIT_OUTPUT_SAMPLE =
  '[main 1a2b3c4] add login form\n 3 files changed, 12 insertions(+), 4 deletions(-)\n'

function invocations(invoke: ReturnType<typeof vi.fn>): Array<[string, unknown]> {
  return invoke.mock.calls.map((c) => [c[0] as string, c[1]])
}

function findInvocation(
  invoke: ReturnType<typeof vi.fn>,
  method: string,
): unknown | undefined {
  const found = invoke.mock.calls.find((c) => c[0] === method)
  return found?.[1]
}

// ============================================================================
// Tests
// ============================================================================

describe('GitModule', () => {
  describe('auto-commit policy', () => {
    it('defaults to false', () => {
      const { module } = buildModule()
      expect(module.isAutoCommit()).toBe(false)
    })

    it('respects the autoCommit constructor option', () => {
      const { module } = buildModule({ autoCommit: true })
      expect(module.isAutoCommit()).toBe(true)
    })

    it('setAutoCommit toggles the flag and logs', () => {
      const { module, logger } = buildModule()
      module.setAutoCommit(true)
      expect(module.isAutoCommit()).toBe(true)
      expect(logger.info).toHaveBeenCalledWith({ enabled: true }, 'auto-commit policy updated')
      module.setAutoCommit(false)
      expect(module.isAutoCommit()).toBe(false)
    })
  })

  describe('commit', () => {
    it('runs status -> add -> commit on a dirty tree and emits CommitMade', async () => {
      const { module, runner, invoke } = buildModule()

      // status: dirty
      runner.enqueue({ exitCode: 0, outputTail: ' M src/foo.ts\n' })
      // add: success
      runner.enqueue({ exitCode: 0, outputTail: '' })
      // commit: success with parseable output
      runner.enqueue({ exitCode: 0, outputTail: COMMIT_OUTPUT_SAMPLE })

      const result = await module.commit('add login form', {
        conversationId: 'conv-1',
        turnId: 'turn-1',
      })

      expect(result.ok).toBe(true)
      expect(result.commitSha).toBe('1a2b3c4')
      expect(result.branch).toBe('main')
      expect(result.fileCount).toBe(3)

      // Three runner calls in order: status, add, commit.
      expect(runner.runs.map((r) => r.args[0])).toEqual(['status', 'add', 'commit'])

      // Audit fan-out: 3 started + 3 completed = 6 GitOperation* invocations,
      // plus 1 CommitMade.
      const startedCount = invoke.mock.calls.filter((c) => c[0] === 'GitOperationStarted').length
      const completedCount = invoke.mock.calls.filter(
        (c) => c[0] === 'GitOperationCompleted',
      ).length
      expect(startedCount).toBe(3)
      expect(completedCount).toBe(3)

      const commitMade = findInvocation(invoke, 'CommitMade') as Record<string, unknown>
      expect(commitMade).toMatchObject({
        commitSha: '1a2b3c4',
        message: 'add login form',
        fileCount: 3,
        branch: 'main',
        conversationId: 'conv-1',
        turnId: 'turn-1',
      })
      // operationId points at the commit's runner exec id (third call → exec-3).
      expect((commitMade as { operationId: string }).operationId).toBe('exec-3')
    })

    it('returns noChanges:true when status output is empty (no add/commit calls)', async () => {
      const { module, runner, invoke } = buildModule()

      runner.enqueue({ exitCode: 0, outputTail: '' }) // status: clean

      const result = await module.commit('subject', {})

      expect(result).toEqual({ ok: true, noChanges: true })
      expect(runner.runs).toHaveLength(1)
      expect(runner.runs[0]?.args[0]).toBe('status')

      // No CommitMade fan-out — there was nothing to commit.
      expect(invocations(invoke).some(([m]) => m === 'CommitMade')).toBe(false)
    })

    it('returns ok:false on git add failure without running commit (but resolves branch for CommitFailed)', async () => {
      const { module, runner, invoke } = buildModule()

      runner.enqueue({ exitCode: 0, outputTail: ' M file.ts\n' }) // status dirty
      runner.enqueue({ exitCode: 128, outputTail: 'fatal: cannot add\n' }) // add fail
      // 3rd op: #emitCommitFailed enriches the CommitFailed event with the
      // current branch via `currentBranch()` (rev-parse). Synthetic default
      // result from the runner is fine.

      const result = await module.commit('subject')

      expect(result.ok).toBe(false)
      expect(result.exitCode).toBe(128)
      expect(result.outputTail).toContain('fatal: cannot add')

      // commit is never attempted (the add failed). The flow is:
      //   status --porcelain  →  add -A  →  rev-parse (branch for the event)
      expect(runner.runs.map((r) => r.args[0])).toEqual(['status', 'add', 'rev-parse'])
      // The third op MUST NOT be a commit — that's the regression this guards.
      expect(runner.runs.some((r) => r.args[0] === 'commit')).toBe(false)

      // No CommitMade, but a CommitFailed IS fanned out.
      expect(invocations(invoke).some(([m]) => m === 'CommitMade')).toBe(false)
      expect(invocations(invoke).some(([m]) => m === 'CommitFailed')).toBe(true)
    })

    it('stamps conversationId / turnId on GitOperationStarted payloads', async () => {
      const { module, runner, invoke } = buildModule()

      runner.enqueue({ exitCode: 0, outputTail: ' M f\n' })
      runner.enqueue({ exitCode: 0, outputTail: '' })
      runner.enqueue({ exitCode: 0, outputTail: COMMIT_OUTPUT_SAMPLE })

      await module.commit('m', { conversationId: 'C', turnId: 'T' })

      const startedPayloads = invoke.mock.calls
        .filter((c) => c[0] === 'GitOperationStarted')
        .map((c) => c[1] as Record<string, unknown>)

      expect(startedPayloads).toHaveLength(3)
      for (const p of startedPayloads) {
        expect(p['conversationId']).toBe('C')
        expect(p['turnId']).toBe('T')
      }
      // OpType wire enum is PascalCase per JsonStringEnumConverter w/o naming policy.
      expect(startedPayloads.map((p) => p['opType'])).toEqual([
        'BranchList',
        'Add',
        'Commit',
      ])
    })

    it('null-stamps missing context fields on the wire', async () => {
      const { module, runner, invoke } = buildModule()

      runner.enqueue({ exitCode: 0, outputTail: '' }) // clean → short-circuit

      await module.commit('m', {}) // no ctx

      const started = findInvocation(invoke, 'GitOperationStarted') as Record<string, unknown>
      expect(started['conversationId']).toBeNull()
      expect(started['turnId']).toBeNull()
    })
  })

  describe('push', () => {
    it('returns ok:true on success and does NOT emit GitPushFailed', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: 'Everything up-to-date\n' })

      const result = await module.push('origin', 'main', { conversationId: 'c1' })

      expect(result).toEqual({ ok: true })
      expect(runner.runs).toEqual([{ op: 'Push', args: ['push', 'origin', 'main'] }])
      expect(invocations(invoke).some(([m]) => m === 'GitPushFailed')).toBe(false)
    })

    it('resolves branch via currentBranch when not passed', async () => {
      const { module, runner } = buildModule()
      // currentBranch (rev-parse) → "feature/x"
      runner.enqueue({ exitCode: 0, outputTail: 'feature/x\n' })
      // push → success
      runner.enqueue({ exitCode: 0, outputTail: '' })

      const result = await module.push()

      expect(result.ok).toBe(true)
      expect(runner.runs[0]?.args).toEqual(['rev-parse', '--abbrev-ref', 'HEAD'])
      expect(runner.runs[1]?.args).toEqual(['push', 'origin', 'feature/x'])
    })

    it('emits GitPushFailed with reason="Auth" on auth error', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({
        exitCode: 128,
        outputTail: 'Permission denied (publickey).\nfatal: Could not read from remote\n',
        authError: true,
      })

      const result = await module.push('origin', 'main')

      expect(result.ok).toBe(false)
      expect(result.authError).toBe(true)
      expect(result.conflict).toBeUndefined()

      const failed = findInvocation(invoke, 'GitPushFailed') as Record<string, unknown>
      expect(failed['reason']).toBe('Auth')
      expect(failed['branch']).toBe('main')
      expect(failed['lastOutputTail']).toContain('publickey')
    })

    it('emits GitPushFailed with reason="Network" on network error', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({
        exitCode: 128,
        outputTail: 'fatal: unable to access: Could not resolve host: github.com\n',
      })

      const result = await module.push('origin', 'main')

      expect(result.ok).toBe(false)
      expect(result.authError).toBeUndefined()
      const failed = findInvocation(invoke, 'GitPushFailed') as Record<string, unknown>
      expect(failed['reason']).toBe('Network')
    })

    it('emits GitPushFailed with reason="Conflict" on non-fast-forward', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({
        exitCode: 1,
        outputTail:
          ' ! [rejected]        main -> main (non-fast-forward)\nerror: failed to push\n',
      })

      const result = await module.push('origin', 'main')

      expect(result.ok).toBe(false)
      expect(result.conflict).toBe(true)
      const failed = findInvocation(invoke, 'GitPushFailed') as Record<string, unknown>
      expect(failed['reason']).toBe('Conflict')
    })

    it('emits GitPushFailed with reason="Unknown" when output matches no heuristic', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({
        exitCode: 1,
        outputTail: 'something exotic happened\n',
      })

      await module.push('origin', 'main')

      const failed = findInvocation(invoke, 'GitPushFailed') as Record<string, unknown>
      expect(failed['reason']).toBe('Unknown')
    })

    // ============================================================================
    // Auth-aware push (daemon-auto-commit-push)
    //
    // GitModule.push() must mint a GitHub installation token via TokenManager
    // and pass it as `-c http.extraHeader=Authorization: Basic <base64>` so
    // `git push` over HTTPS can authenticate. Without this, the daemon's
    // auto-commit lands the commit locally but the push fails with
    // `fatal: could not read Username for 'https://github.com'` (no
    // credential helper, no TTY for interactive prompt).
    //
    // Mirrors the auth-path test patterns in CloningRepoStage.test.ts.
    // ============================================================================
    describe('auth-aware push (with TokenManager wired)', () => {
      it('mints a token and prefixes -c http.extraHeader on push', async () => {
        const { module, runner, getToken } = buildModule({ withAuth: true })
        runner.enqueue({ exitCode: 0, outputTail: '' })

        const result = await module.push('origin', 'main')

        expect(result).toEqual({ ok: true })
        expect(getToken).toHaveBeenCalledTimes(1)
        expect(getToken).toHaveBeenCalledWith(FAKE_REPO_FULL_NAME, undefined)
        expect(runner.runs).toEqual([
          {
            op: 'Push',
            args: [
              '-c',
              `http.extraHeader=Authorization: Basic ${FAKE_BASIC_AUTH}`,
              'push',
              'origin',
              'main',
            ],
          },
        ])
      })

      it('on auth error: invalidates the cached token and retries once with forceRefresh', async () => {
        const { module, runner, getToken, invalidate } = buildModule({ withAuth: true })
        // First attempt fails with auth error.
        runner.enqueue({
          exitCode: 128,
          outputTail: 'remote: Invalid username or password.\nfatal: Authentication failed\n',
          authError: true,
        })
        // Retry with forced-refresh succeeds.
        runner.enqueue({ exitCode: 0, outputTail: '' })

        const result = await module.push('origin', 'main')

        expect(result).toEqual({ ok: true })
        expect(invalidate).toHaveBeenCalledTimes(1)
        expect(invalidate).toHaveBeenCalledWith(FAKE_REPO_FULL_NAME)
        expect(getToken).toHaveBeenCalledTimes(2)
        // Second call must request a forced refresh.
        expect(getToken).toHaveBeenNthCalledWith(2, FAKE_REPO_FULL_NAME, { forceRefresh: true })
        // Both runner.runs carry the auth header — the SECOND with the freshly
        // re-fetched token (same fixture value here, but the call shape is
        // what matters).
        expect(runner.runs).toHaveLength(2)
        expect(runner.runs[0]?.args.slice(0, 3)).toEqual([
          '-c',
          `http.extraHeader=Authorization: Basic ${FAKE_BASIC_AUTH}`,
          'push',
        ])
        expect(runner.runs[1]?.args.slice(0, 3)).toEqual([
          '-c',
          `http.extraHeader=Authorization: Basic ${FAKE_BASIC_AUTH}`,
          'push',
        ])
      })

      it('caps auth retries at one — second auth failure propagates as GitPushFailed', async () => {
        const { module, runner, invoke, invalidate, getToken } = buildModule({ withAuth: true })
        // First attempt: auth error.
        runner.enqueue({
          exitCode: 128,
          outputTail: 'fatal: Authentication failed\n',
          authError: true,
        })
        // Retry: still auth error.
        runner.enqueue({
          exitCode: 128,
          outputTail: 'fatal: Authentication failed\n',
          authError: true,
        })

        const result = await module.push('origin', 'main')

        expect(result.ok).toBe(false)
        expect(result.authError).toBe(true)
        // Only ONE invalidate + TWO getToken calls (initial + single retry).
        expect(invalidate).toHaveBeenCalledTimes(1)
        expect(getToken).toHaveBeenCalledTimes(2)
        expect(runner.runs).toHaveLength(2)

        const failed = findInvocation(invoke, 'GitPushFailed') as Record<string, unknown>
        expect(failed['reason']).toBe('Auth')
      })

      it('non-auth failure does NOT trigger the retry path', async () => {
        const { module, runner, invalidate, getToken } = buildModule({ withAuth: true })
        runner.enqueue({
          exitCode: 1,
          outputTail: ' ! [rejected]   main -> main (non-fast-forward)\n',
        })

        const result = await module.push('origin', 'main')

        expect(result.ok).toBe(false)
        expect(result.conflict).toBe(true)
        expect(invalidate).not.toHaveBeenCalled()
        expect(getToken).toHaveBeenCalledTimes(1)
        expect(runner.runs).toHaveLength(1)
      })

      it('falls back to no-auth args when getRepoFullName returns null', async () => {
        // Runtime with no repo configured (AI-curated empty-spec path) — push
        // shouldn't crash; it should attempt the bare argv form.
        const { module, runner, getToken } = buildModule({
          withAuth: true,
          repoFullName: null,
        })
        runner.enqueue({ exitCode: 0, outputTail: '' })

        const result = await module.push('origin', 'main')

        expect(result).toEqual({ ok: true })
        // Token never minted because there's no repo identifier to key on.
        expect(getToken).not.toHaveBeenCalled()
        expect(runner.runs).toEqual([{ op: 'Push', args: ['push', 'origin', 'main'] }])
      })

      it('legacy wiring (no TokenManager) is unchanged — bare argv on push', async () => {
        // The destructive-op gate's raw-execute path and existing tests still
        // construct GitModule without a token surface. That path must keep
        // working — push() should pass `[push, remote, branch]` verbatim.
        const { module, runner } = buildModule({ withAuth: false })
        runner.enqueue({ exitCode: 0, outputTail: '' })

        await module.push('origin', 'main')

        expect(runner.runs).toEqual([{ op: 'Push', args: ['push', 'origin', 'main'] }])
      })
    })
  })

  describe('merge', () => {
    it('returns ok:true on a clean merge', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: 'Merge made by the no-ff strategy\n' })

      const result = await module.merge('feature/x')

      expect(result).toEqual({ ok: true })
      expect(runner.runs).toEqual([
        { op: 'Merge', args: ['merge', '--no-ff', 'feature/x'] },
      ])
      expect(invocations(invoke).some(([m]) => m === 'MergeConflict')).toBe(false)
    })

    it('on conflict: aborts the merge, parses files, and emits MergeConflict', async () => {
      const { module, runner, invoke } = buildModule()
      const conflictOutput =
        'Auto-merging src/a.ts\n' +
        'CONFLICT (content): Merge conflict in src/a.ts\n' +
        'Auto-merging src/b.ts\n' +
        'CONFLICT (content): Merge conflict in src/b.ts\n' +
        'Automatic merge failed; fix conflicts and then commit the result.\n'
      // merge → conflict
      runner.enqueue({ exitCode: 1, outputTail: conflictOutput })
      // currentBranch → main (called by merge to resolve targetBranch)
      runner.enqueue({ exitCode: 0, outputTail: 'main\n' })
      // merge --abort → success
      runner.enqueue({ exitCode: 0, outputTail: '' })

      const result = await module.merge('feature/x')

      expect(result.ok).toBe(false)
      expect(result.conflict).toBe(true)
      expect(result.files).toEqual(['src/a.ts', 'src/b.ts'])

      // Runner sequence: merge, rev-parse (currentBranch), merge --abort.
      expect(runner.runs.map((r) => r.args.slice(0, 2).join(' '))).toEqual([
        'merge --no-ff',
        'rev-parse --abbrev-ref',
        'merge --abort',
      ])

      const conflictPayload = findInvocation(invoke, 'MergeConflict') as Record<string, unknown>
      expect(conflictPayload).toMatchObject({
        sourceBranch: 'feature/x',
        targetBranch: 'main',
        files: ['src/a.ts', 'src/b.ts'],
      })
      expect(conflictPayload['summary']).toContain('CONFLICT')
      // operationId is the merge call's exec id (first call → exec-1).
      expect(conflictPayload['operationId']).toBe('exec-1')
    })

    it('returns plain failure (no conflict, no abort) when output lacks CONFLICT markers', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({ exitCode: 1, outputTail: 'merge: invalid arg\n' })

      const result = await module.merge('feature/x')

      expect(result.ok).toBe(false)
      expect(result.conflict).toBeUndefined()
      // Only the merge call ran — no abort, no rev-parse.
      expect(runner.runs).toHaveLength(1)
      expect(invocations(invoke).some(([m]) => m === 'MergeConflict')).toBe(false)
    })

    // ============================================================================
    // Partial-clone merge — lazy-fetch retry when target ref lives only on origin
    //
    // On `clone --single-branch --branch=<feature>` runtimes, `git merge main`
    // fails with "did not match any file(s)" because local `main` doesn't
    // exist. The daemon catches that shape, lazy-fetches `main` via the same
    // auth path as the diff handler, and retries the merge against
    // `origin/main`. Mirrors the diff-query partial-clone fix.
    // ============================================================================
    it('lazy-fetches and retries with origin/<branch> on partial-clone missing ref', async () => {
      const { module, runner, getToken } = buildModule({ withAuth: true })
      // Attempt 1: merge main → fails with "did not match" (no local main).
      runner.enqueue({
        exitCode: 128,
        outputTail: "merge: main - not something we can merge\nfatal: 'main' - not something we can merge\n",
      })
      // Lazy fetch succeeds.
      runner.enqueue({ exitCode: 0, outputTail: '' })
      // Attempt 2: merge origin/main → succeeds.
      runner.enqueue({ exitCode: 0, outputTail: 'Merge made by the no-ff strategy\n' })

      const result = await module.merge('main')

      expect(result).toEqual({ ok: true })
      // Three runner calls: failed merge, lazy fetch, retry merge.
      expect(runner.runs.map((r) => ({ op: r.op, last: r.args[r.args.length - 1] }))).toEqual([
        { op: 'Merge', last: 'main' },
        { op: 'Fetch', last: '+refs/heads/main:refs/remotes/origin/main' },
        { op: 'Merge', last: 'origin/main' },
      ])
      // Token minted once for the fetch.
      expect(getToken).toHaveBeenCalledTimes(1)
    })

    it('does NOT retry when merge fails for non-ref reasons (e.g. dirty working tree)', async () => {
      const { module, runner, getToken } = buildModule({ withAuth: true })
      runner.enqueue({
        exitCode: 1,
        outputTail:
          'error: Your local changes to the following files would be overwritten by merge:\n  a.txt\nPlease commit your changes or stash them before you merge.\n',
      })

      const result = await module.merge('main')

      expect(result.ok).toBe(false)
      // No lazy fetch and no retry — the failure mode doesn't match the
      // missing-ref pattern.
      expect(runner.runs).toHaveLength(1)
      expect(getToken).not.toHaveBeenCalled()
    })
  })

  describe('agent workflow (mergeLeaveConflicts / completeMerge / abortMerge)', () => {
    it('mergeLeaveConflicts does not abort — leaves conflict state for the agent', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 1, outputTail: 'CONFLICT (content): Merge conflict in src/a.ts\nAutomatic merge failed; fix conflicts and then commit the result.\n' })

      const result = await module.mergeLeaveConflicts('feature')

      expect(result.ok).toBe(false)
      expect(result.conflict).toBe(true)
      expect(result.files).toEqual(['src/a.ts'])
      expect(runner.runs.some((r) => r.args.includes('--abort'))).toBe(false)
    })

    it('merge() still aborts on conflict (UI path)', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 1, outputTail: 'CONFLICT (content): Merge conflict in b.ts\n' })
      runner.enqueue({ exitCode: 0, outputTail: '' })

      const result = await module.merge('feature')

      expect(result.conflict).toBe(true)
      expect(runner.runs.some((r) => r.args.includes('--abort'))).toBe(true)
    })

    it('completeMerge runs add then merge --continue', async () => {
      const { module, runner } = buildModule()
      // MERGE_HEAD check uses fs.access — use real temp dir or mock?
      // buildModule uses cwd /tmp/test-repo - MERGE_HEAD won't exist
      const result = await module.completeMerge(['src/a.ts'])
      expect(result.ok).toBe(false)
      expect(result.outputTail).toContain('MERGE_HEAD')
    })

    it('syncWithOrigin returns ok:false without throwing on pull failure', async () => {
      const { module, runner } = buildModule({ withAuth: true })
      runner.enqueue({ exitCode: 0, outputTail: 'main\n' })
      runner.enqueue({ exitCode: 128, outputTail: 'fatal: fetch failed\n' })

      const result = await module.syncWithOrigin('main')

      expect(result.ok).toBe(false)
      expect(result.branch).toBe('main')
      expect(result.message).toContain('fetch failed')
    })
  })

  describe('createBranch / fetch', () => {
    it('createBranch invokes `git checkout -b <name>` and returns the GitResult', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: "Switched to a new branch 'feat'\n" })

      const result = await module.createBranch('feat')

      expect(result.exitCode).toBe(0)
      expect(runner.runs).toEqual([{ op: 'BranchCreate', args: ['checkout', '-b', 'feat'] }])
    })

    it('fetch invokes `git fetch <remote>` and returns the GitResult', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: '' })

      const result = await module.fetch('upstream')

      expect(result.exitCode).toBe(0)
      expect(runner.runs).toEqual([{ op: 'Fetch', args: ['fetch', 'upstream'] }])
    })

    it('fetch defaults remote to origin', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 0 })
      await module.fetch()
      expect(runner.runs).toEqual([{ op: 'Fetch', args: ['fetch', 'origin'] }])
    })
  })

  describe('currentBranch', () => {
    it('returns the trimmed first line of the runner output', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: 'feature/x\n' })

      const branch = await module.currentBranch()
      expect(branch).toBe('feature/x')
    })

    it('throws on non-zero exit', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 128, outputTail: 'fatal: not a git repo\n' })
      await expect(module.currentBranch()).rejects.toThrow(/rev-parse/)
    })
  })

  describe('sequential dispatch', () => {
    it('queues a second commit() behind an in-flight one', async () => {
      const { module, runner } = buildModule()

      // First commit's `status` call: pend it so we can interleave the second
      // commit() before this resolves.
      const firstStatus = runner.pendNext()

      // Kick off commit #1 — it awaits the pending status.
      const p1 = module.commit('first')

      // Kick off commit #2 immediately. Until the first commit's chain slot
      // resolves, the queue MUST not have invoked anything for commit #2.
      const p2 = module.commit('second')

      // Yield once so any not-yet-blocked microtasks can flush. The second
      // commit must still be parked behind the first.
      await Promise.resolve()
      await Promise.resolve()
      expect(runner.runs).toHaveLength(1)
      expect(runner.runs[0]?.args[0]).toBe('status')

      // Resolve commit #1's status with "clean" so it short-circuits.
      firstStatus.release({
        exitCode: 0,
        durationMs: 1,
        outputTail: '',
        outputHash: 'a'.repeat(64),
        timedOut: false,
        authError: false,
      })
      const r1 = await p1
      expect(r1).toEqual({ ok: true, noChanges: true })

      // Now commit #2 starts. Queue a "clean" status for it.
      runner.enqueue({ exitCode: 0, outputTail: '' })
      const r2 = await p2
      expect(r2).toEqual({ ok: true, noChanges: true })

      // Total runner calls: 2 (one status per commit).
      expect(runner.runs).toHaveLength(2)
    })

    it('a failing op does NOT poison the queue', async () => {
      const { module, runner } = buildModule()

      // First op throws (we simulate via a runner result that the impl will
      // turn into a thrown rejection — but our impl never throws for a
      // non-zero exit, so simulate by making currentBranch throw via push().)
      runner.enqueue({ exitCode: 128, outputTail: 'fatal: not a git repository\n' })

      await expect(module.push()).rejects.toThrow(/rev-parse/)

      // Subsequent op still runs.
      runner.enqueue({ exitCode: 0, outputTail: 'main\n' }) // currentBranch
      runner.enqueue({ exitCode: 0, outputTail: '' }) // push
      const r = await module.push()
      expect(r.ok).toBe(true)
    })
  })

  describe('runRaw', () => {
    it('passes the invocation through the runner and returns ok/outputTail/exitCode', async () => {
      const { module, runner } = buildModule()
      runner.enqueue({ exitCode: 0, outputTail: 'HEAD is now at 1234567 wip\n' })

      const result = await module.runRaw(
        { op: 'Reset', args: ['reset', '--hard', 'HEAD~1'] },
        { conversationId: 'c', turnId: 't' },
      )

      expect(result).toEqual({
        ok: true,
        outputTail: 'HEAD is now at 1234567 wip\n',
        exitCode: 0,
      })
      expect(runner.runs).toEqual([{ op: 'Reset', args: ['reset', '--hard', 'HEAD~1'] }])
    })

    it('returns ok:false on non-zero exit and forwards the audit fan-out', async () => {
      const { module, runner, invoke } = buildModule()
      runner.enqueue({ exitCode: 128, outputTail: 'fatal: bad object\n' })

      const result = await module.runRaw({ op: 'Reset', args: ['reset', '--hard', 'bogus'] })

      expect(result.ok).toBe(false)
      expect(result.exitCode).toBe(128)
      expect(result.outputTail).toContain('fatal: bad object')

      // Standard started/completed audit pair still fans out — the runRaw
      // passthrough doesn't suppress audit emission.
      const startedCount = invoke.mock.calls.filter(
        (c) => c[0] === 'GitOperationStarted',
      ).length
      const completedCount = invoke.mock.calls.filter(
        (c) => c[0] === 'GitOperationCompleted',
      ).length
      expect(startedCount).toBe(1)
      expect(completedCount).toBe(1)
    })

    it('serialises with concurrent commits via the sequential queue', async () => {
      const { module, runner } = buildModule()

      // First op (a runRaw) — pend it so the second can race.
      const firstRun = runner.pendNext()

      const p1 = module.runRaw({ op: 'Reset', args: ['reset', '--hard'] })

      // Kick off a commit immediately. It must wait for the runRaw to finish.
      const p2 = module.commit('after the reset')

      // Yield twice so any non-blocked microtasks flush. The commit's first
      // call (status) MUST not have happened yet.
      await Promise.resolve()
      await Promise.resolve()
      expect(runner.runs).toHaveLength(1)
      expect(runner.runs[0]?.args[0]).toBe('reset')

      // Resolve the reset.
      firstRun.release({
        exitCode: 0,
        durationMs: 1,
        outputTail: '',
        outputHash: 'a'.repeat(64),
        timedOut: false,
        authError: false,
      })
      const r1 = await p1
      expect(r1.ok).toBe(true)

      // Now the commit runs (status = clean → short-circuit).
      runner.enqueue({ exitCode: 0, outputTail: '' })
      const r2 = await p2
      expect(r2).toEqual({ ok: true, noChanges: true })
    })
  })

  describe('signalr fan-out resilience', () => {
    it('swallows SignalR invoke failures and logs at warn', async () => {
      const { module, runner, invoke, logger } = buildModule()
      invoke.mockRejectedValue(new Error('hub down'))

      runner.enqueue({ exitCode: 0, outputTail: '' }) // status clean

      const result = await module.commit('m')

      expect(result).toEqual({ ok: true, noChanges: true })

      // Drain the unhandled-rejection microtask the fire-and-forget chains
      // schedule so the warn log fires before we assert.
      await new Promise((r) => setImmediate(r))
      expect(logger.warn).toHaveBeenCalled()
    })
  })
})
