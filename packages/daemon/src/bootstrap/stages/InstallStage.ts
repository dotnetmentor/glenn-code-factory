// InstallStage — runs the V2 spec's install bash (top-level + per-service)
// with hash-skip caching so subsequent boots don't re-pay the cost.
//
// === Spec context (runtime-spec-v2, Scenario 5 + Card P2) ===
//
// `spec.install` carries one-time setup bash that lives ABOVE the per-boot
// `spec.setup` step: package installs (apt, downloaded binaries, mise
// toolchains). Each `service.install` carries the per-service install bash
// (e.g. download the MongoDB binary, create its data dir).
//
// The stage composes them in spec-array order (top-level first, then services
// in order) into a single blob, SHA-256 hashes it, and compares against the
// hashes the previous successful run persisted to /data/.glenn/install-
// hashes.json. On match → skip. On mismatch (or missing/malformed file) →
// run the blob via `bash -c`, stream stdout/stderr as bootstrap_progress
// events, and on success persist the new hashes atomically.
//
// === Pipeline position ===
//
// Slotted between WritingConfigStage and CloningRepoStage. This is exactly
// where the deleted InstallingRuntimesStage used to sit, and it makes sense:
// install scripts need /data/.glenn/env to exist (for any secrets they
// reference) and need to run BEFORE the repo is cloned because the V1→V2
// migrator bakes `mise install node@22` etc. into the install field, and
// the cloned repo's setup step needs those toolchains on PATH.
//
// === Per-service hash tracking (subtle but important) ===
//
// We store both:
//   - the per-service hash map in /data/.glenn/install-hashes.json (for
//     Card P2.2's live-delta path: when a single service's install changes,
//     it can re-run just that snippet without trashing the whole cache);
//   - the top-level scope hash in the same file.
//
// Card P2.2 will import `InstallHashStore` and compare per-service hashes
// against an incoming delta. We deliberately do NOT track which subset ran
// on this boot — the stage runs the WHOLE blob on any mismatch, which is the
// simpler and safer contract for first-boot. P2.2 is the place to get clever.
//
// === Running as agent + sudo ===
//
// The daemon already runs as the `agent` user inside the runtime container
// (Dockerfile.runtime-base ends with `USER agent`). Install scripts that
// need root privileges must invoke `sudo -n` themselves (the base image is
// configured with passwordless sudo for agent — see Dockerfile and
// /etc/sudoers.d/agent if present). We do NOT prepend sudo here; the spec
// field is freeform user-authored bash, and forcing sudo would break the
// "run as agent" contract for the common case (mise install, mkdir under
// /data, etc.).
//
// TODO(runtime-base-image): When the runtime base image is rebuilt next, add
// /etc/sudoers.d/agent containing `agent ALL=(ALL) NOPASSWD: ALL` so install
// scripts that need `sudo apt-get install -y X` can do so without prompting.
// At the time of writing the Dockerfile.runtime-base doesn't ship this — the
// `agent` user is created with useradd but no sudoers entry. Card P2.2's docs
// note tracks adding it before the V2 cutover lands in production.

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'
import type { IExecutor } from '../../runtime/IExecutor.js'
import {
  RuntimeEventTypes,
  type RuntimeEventEmitter,
} from '../../events/RuntimeEventEmitter.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapState } from '../BootstrapState.js'
import { BootstrapOutputBatcher } from '../BootstrapOutputBatcher.js'
import {
  composeBlob,
  computeSpecHashes,
  sha256Hex,
  type InstallHashStore,
} from '../../runtime/InstallHashStore.js'
import type { RuntimeSpecV2 } from '../../generated/signalr/Source.Features.RuntimeBootstrap.Contracts.js'
import { BOOTSTRAP_DEFAULT_PATH } from '../../runtime/BootstrapEnvironment.js'

const DEFAULT_CWD = '/'
// Re-exported via the shared constant so dry-run + install + setup never drift.
// See BootstrapEnvironment.ts for the history of the typo this prevents.
const DEFAULT_PATH = BOOTSTRAP_DEFAULT_PATH
const DEFAULT_TIMEOUT_MS = 10 * 60_000

/**
 * Read `DAEMON_INSTALL_TIMEOUT_MS` from the daemon's env; fall back to 10 min
 * if absent or unparsable. Kept as a free function so the constructor can be
 * called with an explicit override in tests without polluting `process.env`.
 */
function resolveTimeoutFromEnv(): number {
  const raw = process.env['DAEMON_INSTALL_TIMEOUT_MS']
  if (raw === undefined) return DEFAULT_TIMEOUT_MS
  const n = Number.parseInt(raw, 10)
  if (!Number.isFinite(n) || n <= 0) return DEFAULT_TIMEOUT_MS
  return n
}

export interface InstallStageDeps {
  signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  state: BootstrapState
  executor: IExecutor
  hashStore: InstallHashStore
  /**
   * Structured RuntimeEvent emitter (runtime-spec-v2). When provided, the
   * stage emits InstallStarted / InstallCompleted / InstallSkipped /
   * InstallFailed events with timing. Optional for back-compat with tests
   * that don't yet provide one — production wiring always provides it.
   */
  emitter?: RuntimeEventEmitter
  /** Override the temp script dir (default `/tmp`). */
  tmpDir?: string
  /** Override cwd for the install bash (default `/`). */
  cwd?: string
  /** Override the PATH passed to install bash. */
  path?: string
  /** Override the timeout. Falls back to DAEMON_INSTALL_TIMEOUT_MS env / 10 min default. */
  timeoutMs?: number
  /**
   * Monotonic clock for timing logs. Defaulted to `Date.now`; tests override
   * to make duration assertions deterministic.
   */
  now?: () => number
}

export class InstallStage implements BootstrapStage {
  readonly name = 'install'
  // NON-CRITICAL (spec) stage: a deterministic install failure must NOT abort
  // the boot. The orchestrator records a BootIssue + continues so the runtime
  // reaches Online (degraded). See self-healing-runtime-specs card D1.
  readonly critical = false

  readonly #signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #executor: IExecutor
  readonly #hashStore: InstallHashStore
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #tmpDir: string
  readonly #cwd: string
  readonly #path: string
  readonly #timeoutMs: number
  readonly #now: () => number

  constructor(deps: InstallStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#executor = deps.executor
    this.#hashStore = deps.hashStore
    this.#emitter = deps.emitter
    this.#tmpDir = deps.tmpDir ?? '/tmp'
    this.#cwd = deps.cwd ?? DEFAULT_CWD
    this.#path = deps.path ?? DEFAULT_PATH
    this.#timeoutMs = deps.timeoutMs ?? resolveTimeoutFromEnv()
    this.#now = deps.now ?? Date.now
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    const spec = this.#state.payload.runtimeSpec
    const blob = composeBlob(spec)

    // No-op path. Empty top-level + empty per-service install → nothing to
    // hash, nothing to write. Still emit a `skipped` event so the timeline
    // shows the stage ran (and didn't quietly disappear).
    if (blob.length === 0) {
      ctx.logger.info('no install bash — skipping')
      void this.#emit(ctx, 'skipped', 'no install bash')
      // Use a tiny zero-duration timer for the structured runtime event so
      // both Started and Skipped land in the timeline.
      const skipTimer = this.#emitter?.startTimer(
        RuntimeEventTypes.InstallStarted,
        { reason: 'no install bash' },
      )
      skipTimer?.skip(RuntimeEventTypes.InstallSkipped, {
        reason: 'no install bash',
      })
      return { ok: true }
    }

    const blobHash = sha256Hex(blob)
    const desired = computeSpecHashes(spec)
    let cached
    try {
      cached = await this.#hashStore.read()
    } catch (err) {
      // The store already treats ENOENT / malformed JSON as "empty". An
      // exception here is genuinely unexpected (e.g. fs failure). Log it and
      // proceed as if the cache were empty — re-running install is always
      // safe; skipping when we shouldn't is the only bad outcome.
      ctx.logger.warn({ err }, 'install hash cache read failed; treating as empty')
      cached = { topLevel: '', services: {} as Record<string, string> }
    }

    // Skip path: every per-scope hash matches what we'd compute today.
    // Comparing per-scope (not the whole blob) is intentional — it lets
    // Card P2.2 re-use the same store for partial deltas without us ever
    // having to recompute the blob hash at apply time.
    if (hashesMatch(cached, desired)) {
      // installVerify gate — belt-and-suspenders for the ~1% case where the
      // hash store on /data survives but the rootfs binaries vanished
      // beneath us (host migration: Fly recreates the upper overlay layer
      // when migrating the machine to a new host, even with
      // persist_rootfs="always"). Without this gate, the hash says "already
      // installed" while supervisord exec's missing binaries and FATAL-loops.
      //
      // We walk top-level verify then every service's verify in spec order.
      // ANY non-zero exit invalidates the entire skip — we re-run the whole
      // blob (no surgical per-scope re-run) because installs often have
      // cross-scope ordering (top-level mise toolchain, then service initdb,
      // etc.). Cheap to over-install; expensive to under-install.
      const verifyFailure = await this.#runVerify(ctx, spec)
      if (verifyFailure === null) {
        ctx.logger.info({ blobHash }, 'install skipped (hash unchanged, verify ok)')
        void this.#emit(ctx, 'skipped', `hash unchanged (${blobHash.slice(0, 12)})`)
        const skipTimer = this.#emitter?.startTimer(
          RuntimeEventTypes.InstallStarted,
          { hash: blobHash },
        )
        skipTimer?.skip(RuntimeEventTypes.InstallSkipped, {
          hash: blobHash,
          reason: 'hash unchanged',
        })
        return { ok: true }
      }
      // Verify failed — fall through to the run path. Log the reason so the
      // operator can see why we re-installed despite a hash match (this is
      // the "host migration ate my rootfs" telemetry).
      ctx.logger.warn(
        { blobHash, verifyFailure },
        'install hash matched but verify failed — re-running install (rootfs wipe suspected)',
      )
      void this.#emit(
        ctx,
        'progress',
        `verify failed (${verifyFailure.scope}: ${verifyFailure.reason.slice(0, 80)}); re-running install`,
      )
    }

    // Run path. Write the blob to a unique temp file so multiple daemons on
    // the same host (rare but possible during local-docker dev) don't stomp
    // each other's script during the brief window between write and exec.
    const startedAt = this.#now()
    const scriptPath = `${this.#tmpDir}/install-${startedAt}-${process.pid}.sh`
    void this.#emit(
      ctx,
      'started',
      `${blob.length} byte(s), ${(spec.services ?? []).length} service(s)`,
    )
    // Paired structured event with mandatory timing. Started fires now;
    // Completed / Failed below auto-fill durationMs.
    const timer = this.#emitter?.startTimer(RuntimeEventTypes.InstallStarted, {
      hash: blobHash,
      blobBytes: blob.length,
      services: (spec.services ?? []).map((s) => s.name),
    })
    ctx.logger.info(
      {
        blobBytes: blob.length,
        services: (spec.services ?? []).map((s) => s.name),
        scriptPath,
      },
      'install starting',
    )

    // Live output batcher — captures every stdout/stderr line from the install
    // bash and ships them as `BootstrapOutputChunk` events on a 2s / 50-line
    // cadence so the Timeline tab shows live progress instead of going silent
    // between InstallStarted and InstallCompleted. Only wired when we have an
    // emitter (legacy tests construct the stage without one).
    const batcher = this.#emitter
      ? new BootstrapOutputBatcher({ emitter: this.#emitter, stage: 'install' })
      : undefined

    try {
      await this.#executor.run(
        'bash',
        ['-c', `cat > "${scriptPath}" <<'__GLENN_INSTALL_EOF__'\n${blob}__GLENN_INSTALL_EOF__\nbash "${scriptPath}"`],
        {
          cwd: this.#cwd,
          env: {
            PATH: this.#path,
            HOME: process.env['HOME'] ?? '/home/agent',
          },
          timeoutMs: this.#timeoutMs,
          onStdout: (chunk) => {
            batcher?.addStdoutLine(chunk)
            const trimmed = chunk.trim()
            if (trimmed.length > 0) void this.#emit(ctx, 'progress', trimmed)
          },
          onStderr: (chunk) => {
            batcher?.addStderrLine(chunk)
            const trimmed = chunk.trim()
            if (trimmed.length > 0) void this.#emit(ctx, 'progress', trimmed)
          },
        },
      )
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      ctx.logger.warn({ err, scriptPath }, 'install bash failed')
      void this.#emit(ctx, 'failed', reason)
      timer?.fail(RuntimeEventTypes.InstallFailed, {
        hash: blobHash,
        errorMessage: reason,
      })
      // Recoverable: install flakes happen (network blip on apt-get,
      // transient mise registry timeout). The orchestrator's backoff will
      // re-try. If it's a real script bug it'll fail every time and exhaust
      // the retry budget — same outcome, just slower than non-recoverable.
      return {
        ok: false,
        reason: `install bash failed: ${reason}`,
        recoverable: true,
      }
    } finally {
      // Final flush of any partial buffer before we return — guarantees the
      // last few lines of stdout/stderr land on the Timeline even when the
      // bash exits between interval ticks. Idempotent if already disposed.
      batcher?.dispose()
    }

    // Persist the per-scope hash map atomically. If this write fails we still
    // count the install as successful — the script ran, the system is in the
    // right state. The cost is one extra re-run next boot, which is annoying
    // but not broken.
    try {
      await this.#hashStore.write(desired)
    } catch (err) {
      ctx.logger.warn(
        { err },
        'install hash persist failed; install will re-run next boot',
      )
    }

    const durationMs = this.#now() - startedAt
    ctx.logger.info({ durationMs, blobHash }, 'install completed')
    void this.#emit(ctx, 'completed', `${durationMs}ms`)
    timer?.complete(RuntimeEventTypes.InstallCompleted, { hash: blobHash })
    return { ok: true }
  }

  /**
   * Walk the spec's verify predicates (top-level + per-service in spec order).
   * Returns `null` when every verifier present exits 0 (or there are none),
   * meaning the install-skip is safe. Returns the first failing scope +
   * reason when any verifier exits non-zero or throws — the caller treats
   * that as "rootfs wiped, re-run install".
   *
   * Verifiers are run via `bash -c` with the SAME env + PATH the install
   * blob gets (so `command -v mongod` finds binaries on `/data/mise/shims`,
   * `/usr/sbin`, etc.). Each verifier has a short hard timeout — verify is
   * supposed to be cheap (single `command -v`, `[ -x … ]`). A verifier that
   * hangs counts as a failure: better to re-install than to deadlock boot.
   */
  async #runVerify(
    ctx: BootstrapContext,
    spec: RuntimeSpecV2,
  ): Promise<{ scope: string; reason: string } | null> {
    const VERIFY_TIMEOUT_MS = 15_000
    const entries: Array<{ scope: string; cmd: string }> = []
    const topLevel = (spec.installVerify ?? '').trim()
    if (topLevel.length > 0) {
      entries.push({ scope: 'top-level', cmd: topLevel })
    }
    for (const svc of spec.services ?? []) {
      const v = (svc.installVerify ?? '').trim()
      if (v.length > 0) {
        entries.push({ scope: `service:${svc.name}`, cmd: v })
      }
    }

    for (const { scope, cmd } of entries) {
      try {
        await this.#executor.run('bash', ['-c', cmd], {
          cwd: this.#cwd,
          env: {
            PATH: this.#path,
            HOME: process.env['HOME'] ?? '/home/agent',
          },
          timeoutMs: VERIFY_TIMEOUT_MS,
          // Don't allow non-zero exits — that's exactly the signal we're
          // looking for. The executor throws on non-zero, which we catch
          // below and report as the failure reason.
        })
        ctx.logger.debug({ scope, cmd }, 'install verify passed')
      } catch (err) {
        const reason = err instanceof Error ? err.message : String(err)
        return { scope, reason }
      }
    }
    return null
  }

  async #emit(
    ctx: BootstrapContext,
    status: 'started' | 'progress' | 'completed' | 'failed' | 'skipped',
    detail?: string,
  ): Promise<void> {
    try {
      await this.#signalr.reportBootstrapProgress(
        detail !== undefined
          ? { stage: this.name, status, detail }
          : { stage: this.name, status },
      )
    } catch (err) {
      ctx.logger.debug({ err, status }, 'reportBootstrapProgress failed')
    }
  }
}

/**
 * Compare two hash snapshots for full equality. Both the top-level hash and
 * the per-service map must match. A new service in `desired` that isn't in
 * `cached` is a mismatch (install changed → re-run); a service that vanished
 * from `desired` but is still in `cached` is ALSO a mismatch because the spec
 * shape has changed and we'd rather re-run than risk skipping a needed step.
 */
function hashesMatch(
  cached: { topLevel: string; services: Record<string, string> },
  desired: { topLevel: string; services: Record<string, string> },
): boolean {
  if (cached.topLevel !== desired.topLevel) return false
  const cachedNames = Object.keys(cached.services)
  const desiredNames = Object.keys(desired.services)
  if (cachedNames.length !== desiredNames.length) return false
  for (const name of desiredNames) {
    if (cached.services[name] !== desired.services[name]) return false
  }
  return true
}
