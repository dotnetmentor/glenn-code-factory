// CloningRepoStage — clones the project's git repo (or fetches+resets when the
// clone already exists). Skipped entirely when `payload.repo` is null (the
// empty-spec / AI-curated onboarding path).
//
// === Auth model: GitHub App installation tokens over HTTPS ===
//
// Replaces the legacy deploy-key + SSH path. The daemon asks
// `IRuntimeHub.GetRepoAccessToken(repoFullName)` for a short-lived (≤ 1h)
// installation token scoped to the project's repo, then passes it on the
// command line via:
//
//   git -c http.extraHeader="Authorization: Basic <base64(x-access-token:<token>)>" <subcommand …>
//
// === Why Basic, not Bearer ===
//
// GitHub's REST API accepts `Authorization: Bearer <installation-token>`,
// but GitHub's **git HTTP backend** does NOT — it only speaks HTTP Basic
// auth. With a Bearer header, GitHub silently rejects the request, git
// falls back to prompting for credentials, and on a non-TTY runtime that
// produces the famously cryptic
//   `fatal: could not read Username for 'https://github.com': No such device or address`
// (See: docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/
//  authenticating-as-a-github-app-installation#using-an-installation-access-token-to-authenticate-as-an-app-installation
//  — for git operations the documented form is Basic auth with username
//  `x-access-token` and the installation token as the password.)
//
// `-c http.extraHeader=…` is the recommended pattern (over baking the token
// into the URL) because:
//
//   - Git does NOT echo `-c` values in its progress / stderr output, so the
//     token never leaks into the bootstrap_progress stream we fan up to the
//     UI.
//   - The token does not land in the repo's `.git/config` (we'd otherwise
//     have to scrub it after the clone), and it does not end up in shell
//     history if an operator inspects logs.
//
// We also set `GIT_TERMINAL_PROMPT=0` so that any future auth misconfig
// fails fast with a real 401 / clear stderr instead of hanging on an
// interactive prompt that will never receive input.
//
// The TokenManager handles caching + single-flight refresh; this stage only
// asks for a token, optionally invalidates on a 401, and retries once.
//
// === fetch vs clone ===
//
// If `<repoDir>/.git` does NOT exist (cold-volume / first boot), we run a
// synchronous `git clone --depth=1 --branch <b>` on the critical path —
// services can't start without code on disk. One-time tax per volume.
//
// If `<repoDir>/.git` exists (warm-volume — suspend→wake, force-respawn,
// post-crash respawn), the daemon does NOT pull from origin on the bootstrap
// path. The volume is the source of truth. The two cases we DO handle here:
//
//   1. Wake-up case (HEAD already on the expected branch): no-op. The
//      runtime's working tree is exactly what the user left behind; the
//      first `StartTurn` carrying `pullBeforeStart=true` is where origin
//      catches up, not here. See the daemon-git-sync-redesign spec.
//
//   2. Forked-volume case (CopyBranch — HEAD on the source branch, expected
//      branch is the destination): a non-destructive `git fetch origin
//      <expected>` followed by `git checkout -B <expected>
//      origin/<expected>`. This is safe-by-construction: CopyBranch pushed
//      the destination branch to origin from the source's tip BEFORE
//      forking the volume, so `origin/<expected>` points at the same SHA
//      that the forked volume's HEAD already has. No reset --hard needed.
//
// === Errors ===
//
// All errors are recoverable (`retryable: true`) — git operations fail
// transiently (network blips, transient auth glitches) more often than
// permanently, and the orchestrator's retry loop is the right escalation.
// The single in-stage 401/403 retry is a faster recovery for the specific
// case of a token that expired mid-operation; it does NOT replace the
// orchestrator retry, it just avoids one round-trip when the failure mode is
// obvious.

import type { access, mkdir, rm } from 'node:fs/promises'

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'
import type { IExecutor } from '../../runtime/IExecutor.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapState } from '../BootstrapState.js'
import type { TokenManager } from '../../github/TokenManager.js'
import type { RuntimeEventEmitter } from '../../events/RuntimeEventEmitter.js'
import { RuntimeEventTypes } from '../../events/RuntimeEventTypes.js'
import { encodeBasicAuth } from '../../github/basicAuth.js'
import { parseRepoFullName } from '../../github/repoFullName.js'

const DEFAULT_REPO_DIR = '/data/project/repo'
const DEFAULT_TIMEOUT_MS = 5 * 60_000

/**
 * Subset of `node:fs/promises` we touch. Test seam — production passes the
 * real fs/promises module. `chmod` and `writeFile` are gone with the deploy
 * key.
 */
export interface CloningRepoStageFs {
  mkdir: typeof mkdir
  access: typeof access
  rm: typeof rm
}

export interface CloningRepoStageDeps {
  signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  state: BootstrapState
  executor: IExecutor
  fs: CloningRepoStageFs
  tokenManager: TokenManager
  /**
   * Optional structured-event emitter. When provided, the stage emits a
   * `BootstrapStageStarted` / `BootstrapStageCompleted` pair per substep
   * (`cloning-repo:fetch-token`, `cloning-repo:git-fetch`,
   * `cloning-repo:git-reset`, `cloning-repo:reconcile-branch`,
   * `cloning-repo:git-clone`) so the `RuntimeEvents` audit table carries
   * sub-stage timing in addition to the outer stage's `durationMs`. The
   * cloning stage dominates wake-from-suspended latency (12-21s observed)
   * and the breakdown turns "why is it slow" into a directly queryable
   * answer instead of a guess. Optional so existing test seams that don't
   * care about timing instrumentation keep working.
   */
  emitter?: RuntimeEventEmitter
  /** Override repo dir for tests (default `/data/project/repo`). */
  repoDir?: string
  /** Override timeout for tests (default 5 min). */
  timeoutMs?: number
}

/**
 * Pattern matching auth-failure indicators in git stderr. Hit on any of these,
 * we invalidate the cached token and retry once with a forced refresh.
 *
 * The "could not read Username" pattern catches the case where GitHub's git
 * backend rejected our header (auth failure) and git fell back to prompting
 * for credentials. With `GIT_TERMINAL_PROMPT=0` set we should normally see
 * the underlying 401 instead, but the pattern is here for defence-in-depth:
 * on some git builds the prompt error wins the race for stderr.
 */
const AUTH_FAILURE_PATTERNS = [
  /\b401\b/i,
  /\b403\b/i,
  /authentication failed/i,
  /bad credentials/i,
  /could not read username/i,
] as const

function looksLikeAuthFailure(message: string): boolean {
  return AUTH_FAILURE_PATTERNS.some((re) => re.test(message))
}

export class CloningRepoStage implements BootstrapStage {
  readonly name = 'cloning-repo'

  readonly #signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #executor: IExecutor
  readonly #fs: CloningRepoStageFs
  readonly #tokenManager: TokenManager
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #repoDir: string
  readonly #timeoutMs: number

  constructor(deps: CloningRepoStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#executor = deps.executor
    this.#fs = deps.fs
    this.#tokenManager = deps.tokenManager
    this.#emitter = deps.emitter
    this.#repoDir = deps.repoDir ?? DEFAULT_REPO_DIR
    this.#timeoutMs = deps.timeoutMs ?? DEFAULT_TIMEOUT_MS
  }

  /**
   * Wrap an async substep with a Started/Completed event pair so the
   * persistent `RuntimeEvents` table carries per-substep `durationMs`.
   *
   * <p>Naming convention: <c>cloning-repo:&lt;substep&gt;</c>. The colon
   * keeps the outer stage's existing <c>BootstrapStageStarted</c> rows
   * (<c>stageName: "cloning-repo"</c>) cleanly separable from substeps in
   * the timeline UI and in ad-hoc SQL (<c>Payload-&gt;&gt;'stageName' LIKE
   * 'cloning-repo:%'</c>).</p>
   *
   * <p>On exception we deliberately do NOT emit a substep
   * <c>BootstrapStageFailed</c> — the outer stage already records its own
   * failure with the full error context, so a duplicate-shaped substep
   * failure event would just add noise to the timeline. The (rare) absent
   * Completed row is itself a useful "this is where it died" signal.</p>
   */
  async #withSubstepTimer<T>(
    substep: string,
    fn: () => Promise<T>,
  ): Promise<T> {
    const timer = this.#emitter?.startTimer(
      RuntimeEventTypes.BootstrapStageStarted,
      { stageName: `${this.name}:${substep}` },
    )
    const value = await fn()
    timer?.complete(RuntimeEventTypes.BootstrapStageCompleted, {
      stageName: `${this.name}:${substep}`,
    })
    return value
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    const repo = this.#state.payload.repo
    if (repo === null) {
      ctx.logger.info('no repo configured — skipping clone')
      void this.#emit(ctx, 'skipped', 'no repo')
      return { ok: true }
    }

    void this.#emit(ctx, 'started', `${repo.url} @ ${repo.branch}`)

    let repoFullName: string
    try {
      repoFullName = parseRepoFullName(repo.url)
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      return { ok: false, reason: `repo url parse failed: ${reason}`, recoverable: true }
    }

    const repoExists = await this.#fileExists(`${this.#repoDir}/.git`)
    // Ensure parent of repo dir exists so `git clone` can create repoDir.
    // For the fetch path this is a no-op (the dir already exists) but a
    // single mkdir keeps the code path tight.
    //
    // Edge case: `<repoDir>` itself exists but `<repoDir>/.git` does not —
    // a previous boot's clone started populating files (lock file, README)
    // and crashed before the .git directory was finalised. `git clone`
    // refuses to clone into a non-empty directory (`fatal: destination
    // path 'X' already exists and is not an empty directory.`), so the
    // stage would loop forever. Recover by wiping the partial tree so the
    // clone has a clean target. This is safe — without .git there's no
    // committable work to preserve.
    try {
      if (!repoExists) {
        const parent = this.#repoDir.replace(/\/[^/]+$/, '') || '/'
        await this.#fs.mkdir(parent, { recursive: true })
        if (await this.#fileExists(this.#repoDir)) {
          ctx.logger.warn(
            { repoDir: this.#repoDir },
            'partial repo dir (no .git) — wiping before fresh clone',
          )
          await this.#fs.rm(this.#repoDir, { recursive: true, force: true })
        }
      }
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', `repo dir prep: ${reason}`)
      return { ok: false, reason: `repo dir prep failed: ${reason}`, recoverable: true }
    }

    try {
      if (repoExists) {
        // === Warm-volume path (suspend→wake, force-respawn, post-crash, or
        // CopyBranch-forked volume) ===
        //
        // Per the daemon-git-sync-redesign spec: the volume is the source of
        // truth once a runtime exists. We do NOT pull from origin here. The
        // first session on a fresh runtime gets an explicit `pullBeforeStart`
        // signal from the backend (handled in TurnRunner, not here).
        //
        // The only thing this path needs to handle is branch alignment for
        // the CopyBranch case: the forked Fly volume arrives with HEAD on
        // the SOURCE branch, but `repo.branch` is the destination branch.
        // We compare current HEAD branch to the expected branch and:
        //
        //   - same branch (the common wake-up case): no-op. The working
        //     tree is exactly what the user left behind.
        //   - different branch (CopyBranch case): non-destructive fetch +
        //     checkout -B. Safe-by-construction because CopyBranch pushed
        //     the destination branch to origin from the source's tip BEFORE
        //     forking the volume, so `origin/<expected>` and the forked
        //     volume's HEAD point at the same SHA — no working-tree
        //     destruction, no reset --hard needed.
        const currentBranch = await this.#readCurrentBranch()
        if (currentBranch === repo.branch) {
          // Wake-up case — Fly volume is the source of truth, no git ops
          // needed. The runtime flips Online in sub-second and the user's
          // work-in-progress is preserved exactly as they left it.
          ctx.logger.info(
            { repoDir: this.#repoDir, branch: repo.branch },
            'warm volume, already on expected branch — skipping reset',
          )
        } else {
          // Forked-volume case (CopyBranch) — non-destructive switch.
          // Authenticated fetch (same auth shape as the clone path), then a
          // local `checkout -B` to force-create the local branch tracking
          // origin/<expected>. We use `-B` (capital) because in the
          // shallow single-branch forked volume, git's --guess auto-create
          // of a local branch from the remote ref doesn't reliably fire,
          // and -B also overwrites any stale local branch from a prior
          // boot.
          ctx.logger.info(
            { repoDir: this.#repoDir, from: currentBranch, to: repo.branch },
            'warm volume on different branch — switching (CopyBranch case)',
          )
          await this.#withSubstepTimer('git-fetch', () =>
            this.#gitWithAuthRetry(ctx, repoFullName, (token) => [
              '-c',
              `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`,
              '-C',
              this.#repoDir,
              'fetch',
              '--depth=1',
              'origin',
              `+refs/heads/${repo.branch}:refs/remotes/origin/${repo.branch}`,
            ]),
          )
          // Local op (no auth needed). Refs are already on disk from the
          // fetch above.
          await this.#withSubstepTimer('git-checkout', () =>
            this.#git(ctx, [
              '-C',
              this.#repoDir,
              'checkout',
              '-B',
              repo.branch,
              `origin/${repo.branch}`,
            ]),
          )
        }
      } else {
        // === Cold-volume path (first boot of a new runtime) ===
        //
        // Services literally cannot start without code on disk, so the
        // clone HAS to be on the critical path. This is a one-time tax
        // per runtime/volume — every subsequent wake takes the warm path
        // above (which does no git ops in the common case).
        ctx.logger.info({ repoDir: this.#repoDir }, 'fresh clone')
        await this.#withSubstepTimer('git-clone', () =>
          this.#gitWithAuthRetry(ctx, repoFullName, (token) => [
            '-c',
            `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`,
            'clone',
            '--depth=1',
            '--branch',
            repo.branch,
            repo.url,
            this.#repoDir,
          ]),
        )
      }
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      return { ok: false, reason: `git failed: ${reason}`, recoverable: true }
    }

    // === Branch reconciliation (copy-branch feature) ===
    //
    // Existed-and-skipped / existed-and-fetched / fresh-clone all converge
    // here. For fresh-clone and warm-boot-on-correct-branch this is a
    // no-op: HEAD already points at the target branch. The case this
    // exists for is a CLONED VOLUME (Fly volume forked from another
    // runtime's volume for the "Copy Branch" feature): the volume arrives
    // with `.git` populated and HEAD pointing at the SOURCE branch (e.g.
    // `payments`), but `repo.branch` is the destination branch name (e.g.
    // `payments-copy`). Without this reconciliation users would see
    // "On branch payments" in their terminal — wrong.
    //
    // Idempotent: on the second boot HEAD is already on the right branch
    // and the short-circuit at step 1 returns immediately.
    try {
      const reconcileResult = await this.#withSubstepTimer('reconcile-branch', () =>
        this.#reconcileBranch(ctx, repoFullName, repo.branch),
      )
      if (!reconcileResult.ok) {
        void this.#emit(ctx, 'failed', reconcileResult.reason)
        return { ok: false, reason: reconcileResult.reason, recoverable: true }
      }
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      return { ok: false, reason: `git failed: ${reason}`, recoverable: true }
    }

    ctx.logger.info({ branch: repo.branch }, 'repo ready')
    void this.#emit(ctx, 'completed')
    return { ok: true }
  }

  /**
   * Ensure the working tree's HEAD is on `targetBranch`. Short-circuits when
   * it already is. Otherwise fetches the ref from origin and switches —
   * needed for the "Copy Branch" feature where a forked Fly volume arrives
   * with HEAD on the source branch (see comment at call site).
   */
  async #reconcileBranch(
    ctx: BootstrapContext,
    repoFullName: string,
    targetBranch: string,
  ): Promise<{ ok: true } | { ok: false; reason: string }> {
    const current = await this.#readCurrentBranch()
    if (current === targetBranch) {
      ctx.logger.debug({ branch: current }, 'HEAD already on target branch — skipping switch')
      return { ok: true }
    }

    ctx.logger.info(
      { from: current, to: targetBranch },
      `Switching from ${current} to ${targetBranch} (cloned volume)`,
    )

    // Fetch the target ref. Backend creates this branch on GitHub before
    // forking the volume (per the copy-branch spec) so origin/{target}
    // should always resolve. Uses the same auth shape as the main clone.
    await this.#gitWithAuthRetry(ctx, repoFullName, (token) => [
      '-c',
      `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`,
      '-C',
      this.#repoDir,
      'fetch',
      '--depth=1',
      'origin',
      `+refs/heads/${targetBranch}:refs/remotes/origin/${targetBranch}`,
    ])

    // Force-create a local branch tracking `origin/<targetBranch>`. We use
    // `switch -C` (capital) rather than a bare `switch <branch>` because:
    //   1. In a shallow `--depth=1` single-branch clone whose origin config
    //      is locked to a DIFFERENT branch (the forked-volume case), git's
    //      --guess auto-create of a local branch from the remote ref doesn't
    //      reliably fire and we get `fatal: invalid reference: <branch>`.
    //   2. `-C` (capital) overwrites any pre-existing local branch with that
    //      name — defensive against re-runs and the rare case where a stale
    //      local branch of the same name exists from a prior boot.
    // No auth needed — local operation against already-fetched refs.
    await this.#git(ctx, [
      '-C',
      this.#repoDir,
      'switch',
      '-C',
      targetBranch,
      `origin/${targetBranch}`,
    ])

    // Verify we actually landed on the target branch. A mismatch here is a
    // hard failure (no point continuing bootstrap with the wrong checkout).
    const after = await this.#readCurrentBranch()
    if (after !== targetBranch) {
      return {
        ok: false,
        reason: `branch switch failed: expected HEAD on '${targetBranch}', got '${after}'`,
      }
    }

    // Warn (don't fail) on unexpected dirty tree post-switch. The target
    // ref was created from source's tip SHA, so the working tree should
    // be clean — but a divergence is a smell, not necessarily fatal.
    const status = await this.#executor.run(
      'git',
      ['-C', this.#repoDir, 'status', '--porcelain'],
      { timeoutMs: this.#timeoutMs, env: { GIT_TERMINAL_PROMPT: '0' } },
    )
    if (status.stdout.trim().length > 0) {
      ctx.logger.warn(
        { status: status.stdout.trim() },
        'unexpected working-tree changes after branch switch',
      )
    }

    return { ok: true }
  }

  async #readCurrentBranch(): Promise<string> {
    const result = await this.#executor.run(
      'git',
      ['-C', this.#repoDir, 'rev-parse', '--abbrev-ref', 'HEAD'],
      { timeoutMs: this.#timeoutMs, env: { GIT_TERMINAL_PROMPT: '0' } },
    )
    return result.stdout.trim()
  }

  /**
   * Run git with a freshly-fetched token. On auth-shaped failure (401 / 403
   * / "authentication failed" / "Bad credentials") invalidate the cached
   * token and retry ONCE with a forced refresh. A second failure propagates
   * to the caller (which surfaces it as a recoverable stage failure —
   * orchestrator retry takes it from there).
   */
  async #gitWithAuthRetry(
    ctx: BootstrapContext,
    repoFullName: string,
    buildArgs: (token: string) => readonly string[],
  ): Promise<void> {
    // `fetch-token` is nested inside the outer git-fetch / git-clone
    // substep timer; sum-of-substeps will exceed the outer substep's
    // durationMs intentionally — the breakdown shows token vs network
    // contributions to that outer span.
    const firstToken = await this.#withSubstepTimer('fetch-token', () =>
      this.#tokenManager.getToken(repoFullName),
    )
    try {
      await this.#git(ctx, buildArgs(firstToken))
      return
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      if (!looksLikeAuthFailure(msg)) throw err
      ctx.logger.warn(
        { repoFullName },
        'git auth failure — invalidating token and retrying once',
      )
      this.#tokenManager.invalidate(repoFullName)
    }

    // Force-refresh path. We emit a second `fetch-token` event so the
    // timeline shows BOTH token fetches (the cached miss + the forced
    // refresh) — operators investigating a slow boot can see whether the
    // single-flight refresh happened and how long it took.
    const retryToken = await this.#withSubstepTimer('fetch-token-refresh', () =>
      this.#tokenManager.getToken(repoFullName, { forceRefresh: true }),
    )
    await this.#git(ctx, buildArgs(retryToken))
  }

  async #git(ctx: BootstrapContext, args: readonly string[]): Promise<void> {
    await this.#executor.run('git', args, {
      timeoutMs: this.#timeoutMs,
      env: {
        // Hard-disable git's interactive credential prompt. If auth ever
        // fails (e.g. expired / wrong-scope token) we want to see a real
        // 401 in stderr, not the cryptic "could not read Username for
        // 'https://github.com': No such device or address" that occurs
        // when git's `getpass()` fires on a non-TTY runtime.
        GIT_TERMINAL_PROMPT: '0',
      },
      onStdout: (chunk) => {
        const sanitised = sanitiseGitOutput(chunk)
        if (sanitised.length > 0) void this.#emit(ctx, 'progress', sanitised)
      },
      onStderr: (chunk) => {
        // git writes most progress to stderr — surface those too.
        const sanitised = sanitiseGitOutput(chunk)
        if (sanitised.length > 0) void this.#emit(ctx, 'progress', sanitised)
      },
    })
  }

  async #fileExists(path: string): Promise<boolean> {
    try {
      await this.#fs.access(path)
      return true
    } catch {
      return false
    }
  }

  async #emit(
    ctx: BootstrapContext,
    status: 'started' | 'progress' | 'completed' | 'failed' | 'skipped',
    detail?: string,
  ): Promise<void> {
    try {
      await this.#signalr.reportBootstrapProgress(
        detail !== undefined ? { stage: this.name, status, detail } : { stage: this.name, status },
      )
    } catch (err) {
      ctx.logger.debug({ err, status }, 'reportBootstrapProgress failed')
    }
  }
}

/**
 * Strip any line that mentions an Authorization header before fanning a git
 * progress chunk up as a bootstrap event. Git does not echo `-c` config
 * values in its progress output today, but this is defence-in-depth against
 * a future git release (or a verbose-mode flag) that does.
 */
function sanitiseGitOutput(chunk: string): string {
  const trimmed = chunk.trim()
  if (trimmed.length === 0) return ''
  const filtered = trimmed
    .split('\n')
    .filter((line) => !/authorization\s*:/i.test(line))
    .join('\n')
  return filtered
}
