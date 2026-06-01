// RunningSetupStage — runs the project's setup bash (`npm install`,
// `dotnet restore`, migrations …) against the freshly-cloned repo.
//
// === V2 contract ===
//
// `payload.runtimeSpec.setup` is a single bash string (no longer an array of
// per-line commands). The user authors it as a shell snippet — `&&`, pipes,
// redirects, multiline — and we hand it to `bash -c` verbatim. This is what
// V2 was designed for: freeform user-owned bash, not a pre-parsed token list.
//
// The bash runs in `<repoDir>` with a fixed PATH that includes
// `/data/mise/shims` so the just-installed mise toolchains are reachable. We deliberately
// do NOT inherit the daemon's full `process.env` — the spec calls for a
// sandboxed environment with a known PATH; secrets reach the process via
// `/data/.glenn/env` (which the user's shell or supervisord loads
// downstream — that's a base-image concern, not this stage's).
//
// Failure mode: a non-zero exit fails the stage with `recoverable: true`
// (setup flakes on transient registry / network issues). Skipped entirely
// when `setup` is empty / null / whitespace.
//
// === uid agent ===
//
// The spec calls for running setup as the `agent` user. The current `IExecutor`
// surface doesn't expose a uid switch (the executor binds to the current
// process's uid; the production base image is expected to grant the daemon
// either passwordless `sudo -u agent` or run as agent already). When a future
// card needs to drop privileges here, factor a `SudoExecutor` wrapper as the
// runtime-curation cards do for supervisorctl. TODO(runtime-bootstrap).

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
import { OutputTailBuffer } from '../../logs/OutputTailBuffer.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapState } from '../BootstrapState.js'
import { BootstrapOutputBatcher } from '../BootstrapOutputBatcher.js'
import { BOOTSTRAP_DEFAULT_PATH } from '../../runtime/BootstrapEnvironment.js'

const DEFAULT_REPO_DIR = '/data/project/repo'
// Centralized so dry-run + install + setup never drift.
const DEFAULT_PATH = BOOTSTRAP_DEFAULT_PATH
const DEFAULT_TIMEOUT_MS = 10 * 60_000

export interface RunningSetupStageDeps {
  signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  state: BootstrapState
  executor: IExecutor
  /**
   * Structured RuntimeEvent emitter. When provided, the stage emits
   * SetupCommandStarted / SetupCommandCompleted / SetupCommandFailed events
   * with mandatory timing. Optional for back-compat with older tests.
   */
  emitter?: RuntimeEventEmitter
  /** Override repo dir for tests (default `/data/project/repo`). */
  repoDir?: string
  /** Override the PATH passed to the setup bash. */
  path?: string
  /** Override the timeout for the setup bash (default 10 min). */
  timeoutMs?: number
}

export class RunningSetupStage implements BootstrapStage {
  readonly name = 'running-setup'
  // NON-CRITICAL (spec) stage: a deterministic setup-bash failure must NOT
  // abort the boot. The orchestrator records a BootIssue + continues so the
  // runtime reaches Online (degraded). See self-healing-runtime-specs card D1.
  readonly critical = false

  readonly #signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #executor: IExecutor
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #repoDir: string
  readonly #path: string
  readonly #timeoutMs: number

  constructor(deps: RunningSetupStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#executor = deps.executor
    this.#emitter = deps.emitter
    this.#repoDir = deps.repoDir ?? DEFAULT_REPO_DIR
    this.#path = deps.path ?? DEFAULT_PATH
    this.#timeoutMs = deps.timeoutMs ?? DEFAULT_TIMEOUT_MS
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    const raw = this.#state.payload.runtimeSpec.setup ?? ''
    const setup = raw.trim()
    if (setup.length === 0) {
      ctx.logger.info('no setup bash — skipping')
      void this.#emit(ctx, 'skipped', 'no setup bash')
      // No structured event for a no-op skip — there's no SetupCommandSkipped
      // taxonomy entry. The bootstrap-stage Started/Completed pair already
      // covers the "stage ran but did nothing" case from the orchestrator.
      return { ok: true }
    }

    void this.#emit(ctx, 'started', `${setup.length} char(s) of setup bash`)
    const timer = this.#emitter?.startTimer(
      RuntimeEventTypes.SetupCommandStarted,
      { commandBytes: setup.length },
    )

    const env = { PATH: this.#path, HOME: process.env['HOME'] ?? '/home/agent' }

    // In-memory tail of the setup's stdout+stderr. The Timeline tab in the
    // super-admin runtime drawer attaches the last 30 lines (8KB cap) to the
    // SetupCommandCompleted / SetupCommandFailed payload as `outputTailLines`
    // so the operator can see "what just happened" without opening the Logs
    // tab. Bounded so a chatty `npm install` can't blow the event payload.
    const tail = new OutputTailBuffer()

    // Live output batcher — captures every stdout/stderr line from setup and
    // ships them as `BootstrapOutputChunk` events on a 2s / 50-line cadence so
    // the Timeline tab shows live progress instead of going silent between
    // SetupCommandStarted and SetupCommandCompleted. Independent of the tail
    // buffer above (tail is post-mortem; chunks are live).
    const batcher = this.#emitter
      ? new BootstrapOutputBatcher({ emitter: this.#emitter, stage: 'setup' })
      : undefined

    // The setup field is a freeform bash snippet — `&&`, pipes, redirects, and
    // multiline statements all work. Spawn through `bash -c` so the user's
    // shell semantics are exactly what they wrote.
    try {
      await this.#executor.run('bash', ['-c', setup], {
        cwd: this.#repoDir,
        env,
        timeoutMs: this.#timeoutMs,
        onStdout: (chunk) => {
          tail.pushStdout(chunk)
          batcher?.addStdoutLine(chunk)
          const trimmed = chunk.trim()
          if (trimmed.length > 0) void this.#emit(ctx, 'progress', trimmed)
        },
        onStderr: (chunk) => {
          tail.pushStderr(chunk)
          batcher?.addStderrLine(chunk)
          const trimmed = chunk.trim()
          if (trimmed.length > 0) void this.#emit(ctx, 'progress', trimmed)
        },
      })
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      timer?.fail(RuntimeEventTypes.SetupCommandFailed, {
        errorMessage: reason,
        outputTailLines: tail.take(),
      })
      return {
        ok: false,
        reason: `setup bash failed: ${reason}`,
        recoverable: true,
      }
    } finally {
      // Final flush of any partial buffer before we return — guarantees the
      // last few lines of stdout/stderr land on the Timeline even when the
      // bash exits between interval ticks. Idempotent if already disposed.
      batcher?.dispose()
    }

    ctx.logger.info('setup bash done')
    void this.#emit(ctx, 'completed')
    timer?.complete(RuntimeEventTypes.SetupCommandCompleted, {
      outputTailLines: tail.take(),
    })
    return { ok: true }
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
