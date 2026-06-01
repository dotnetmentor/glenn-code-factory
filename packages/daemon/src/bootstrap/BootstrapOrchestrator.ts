// BootstrapOrchestrator — runs a sequence of bootstrap stages on daemon startup.
//
// Mirrors the design choices of the sibling modules (HeartbeatModule, DiskMonitor):
//
//   - Concrete class, dependencies via constructor, no DI container.
//   - ECMAScript private fields (#) for internal state.
//   - "Failure is data, not exception" — stages return a Result-shaped object.
//     Throwing stages are tolerated (treated as recoverable failures) but
//     stages that know they're hosed are expected to return
//     `{ ok: false, recoverable: false, ... }`.
//
// The orchestrator runs the stages it is given, in order, and emits per-stage
// events to main API so the runtime-status endpoint can show progress. It does
// NOT define any actual stages — those belong to spec `runtime-bootstrap`. The
// two stub stages shipped alongside (`VerifyEnvStage`, `ReportReadyStage`)
// exist purely so this card has something concrete to run + test against.

import type { Logger } from 'pino'

import { DaemonConfig } from '../config/DaemonConfig.js'
import {
  RuntimeEventTypes,
  type RuntimeEventEmitter,
} from '../events/RuntimeEventEmitter.js'
import { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { AgentEventKind } from '../signalr/types.js'
import { BootIssueStore, type BootIssue } from './BootIssueStore.js'

/**
 * Result of running one bootstrap stage. A stage either succeeded (`ok: true`)
 * or failed with a reason and a `recoverable` flag — recoverable failures are
 * retried by the orchestrator, unrecoverable ones abort the bootstrap.
 */
export type BootstrapStageResult =
  | { ok: true }
  | { ok: false; reason: string; recoverable: boolean }

/**
 * Context handed to every stage. Keeps stages decoupled from the orchestrator
 * (and from each other) — a stage takes only what it needs through this single
 * argument.
 */
export type BootstrapContext = {
  config: DaemonConfig
  signalr: SignalRClient
  logger: Logger
  signal: AbortSignal
}

/**
 * BootstrapStage interface. Justified (rather than boilerplate) because the
 * orchestrator runs a polymorphic list of unrelated stages — each stage is its
 * own concrete class with its own dependencies, but they share the run()
 * shape so the orchestrator can iterate.
 */
export interface BootstrapStage {
  readonly name: string
  /**
   * Whether a failure of this stage is FATAL to the boot (self-healing-runtime-
   * specs, card D1).
   *
   *   - `undefined` / `true` → CRITICAL. The stage runs through
   *     `#runStageWithRetries` (up to MAX_ATTEMPTS with backoff) and an
   *     unrecoverable / exhausted failure aborts bootstrap with a
   *     `BootstrapAbortedError`. CRITICAL stages: Connecting, VerifyEnv,
   *     Fetching, WritingConfig, CloningRepo, ReportReady.
   *
   *   - `false` → NON-CRITICAL (a "spec" stage authored from the agent's
   *     runtime spec: Install, RunningSetup, StartingServices). A deterministic
   *     failure does NOT abort the boot — the orchestrator records a
   *     `BootIssue` and CONTINUES, so `ReportReady` still runs and the runtime
   *     reaches Online in a `Degraded` SpecHealth state. We still retry a
   *     bounded number of times for failures that look like TRANSIENT infra
   *     flakes (network blip, apt mirror hiccup) — see `#isTransientReason`.
   *
   * Default semantics live in `#isCritical()` so a stage that simply omits the
   * field keeps the legacy fatal behaviour.
   */
  readonly critical?: boolean
  run(ctx: BootstrapContext): Promise<BootstrapStageResult>
}

/**
 * Thrown when bootstrap can't make progress: either a stage returned an
 * unrecoverable failure, a recoverable stage exhausted its retry budget,
 * the persistent boot-attempt counter exceeded its hard ceiling, or the
 * AbortSignal fired during a backoff wait.
 *
 * `terminal: true` marks failures the supervisor / .NET backend should NOT
 * recover from in the same boot cycle — non-recoverable stage failures and
 * boot-attempt-cap exhaustion both set this. Recoverable-but-exhausted
 * failures within a single process leave it false (the daemon's normal
 * exit path applies; supervisord may legitimately respawn for transient
 * environmental issues that have since cleared).
 */
export class BootstrapAbortedError extends Error {
  readonly stage: string
  readonly reason: string
  readonly attempts: number
  readonly terminal: boolean
  constructor(opts: { stage: string; reason: string; attempts: number; terminal?: boolean }) {
    super(
      `Bootstrap aborted at stage '${opts.stage}' after ${opts.attempts} attempts: ${opts.reason}`,
    )
    this.name = 'BootstrapAbortedError'
    this.stage = opts.stage
    this.reason = opts.reason
    this.attempts = opts.attempts
    this.terminal = opts.terminal ?? false
  }
}

const MAX_ATTEMPTS = 5
// Wait before attempt N+1 (i.e. BACKOFF_MS[0] is the wait after attempt 1
// fails, before attempt 2). With MAX_ATTEMPTS = 5 we have at most 4 waits.
const BACKOFF_MS = [1_000, 2_000, 4_000, 8_000, 30_000] as const

/**
 * Hard ceiling on the persistent `bootAttemptNumber` counter (incremented
 * once per daemon process across supervisord respawns). Once we've tried
 * this many times without a single successful boot, we stop short-circuit
 * before running any stages and surface a terminal failure so the .NET
 * backend can escalate (RespawnRuntimeJob → escalate-to-Failed after a few
 * crashes — see `ScheduleRespawnHandler`).
 *
 * In production we saw a recoverable stage (smoketest-mongo) get retried
 * 104+ times across daemon respawns; this cap prevents that runaway loop.
 */
export const MAX_BOOT_ATTEMPTS = 10

/**
 * Bounded retry budget for NON-CRITICAL (spec) stages whose failure looks like
 * a TRANSIENT infra flake (see `#isTransientReason`). Deliberately small: a
 * real flake clears in a couple of attempts; anything that survives this many
 * retries is treated as a deterministic spec bug and recorded as a BootIssue.
 * The full 5× critical-stage budget would mean a deterministic install bug
 * burns ~45s before the runtime even reaches Online — exactly the coupling D1
 * removes. 2 retries (3 total attempts) reuses the first two BACKOFF_MS slots
 * (1s + 2s).
 */
export const MAX_SPEC_STAGE_RETRIES: number = 2

/**
 * Allowlist of failure-reason signatures we treat as TRANSIENT infra flakes for
 * NON-CRITICAL (spec) stages. Matched case-insensitively against the stage's
 * `reason` string. Kept intentionally narrow — false negatives are cheap (the
 * runtime comes Online degraded and the agent self-heals), false positives cost
 * a few bounded retry seconds. Covers the documented flake vectors: package-
 * mirror / registry hiccups (apt, npm, mise, pip, nuget), DNS / network resets,
 * and generic timeouts.
 */
const TRANSIENT_REASON_PATTERNS: readonly RegExp[] = [
  // Network / DNS / transport
  /\betimedout\b/,
  /\becONNreset\b/i,
  /\beconnrefused\b/,
  /\benotfound\b/,
  /\beai_again\b/,
  /\bconnection reset\b/,
  /\bconnection refused\b/,
  /\bnetwork (?:is )?unreachable\b/,
  /\btemporary failure in name resolution\b/,
  /\btimed? ?out\b/,
  /\btimeout\b/,
  // HTTP transient status codes from package mirrors / bundle fetch
  /\b50[234]\b/,
  /\bservice unavailable\b/,
  /\bbad gateway\b/,
  /\bgateway time-?out\b/,
  /\btoo many requests\b/,
  // Package-manager mirror hiccups
  /\bcould not (?:resolve|connect to) (?:host|.*mirror)/,
  /\bfailed to fetch\b/,
  /\bhash sum mismatch\b/, // apt mirror mid-sync
  /\btemporary failure\b/,
  /\bregistry\b.*\b(?:error|unavailable|timeout)\b/,
  /\bmise\b.*\b(?:download|fetch|network)\b/,
] as const

export class BootstrapOrchestrator {
  readonly #stages: readonly BootstrapStage[]
  readonly #signalr: SignalRClient
  readonly #config: DaemonConfig
  readonly #logger: Logger
  readonly #emitter: RuntimeEventEmitter | undefined
  /** Monotonic clock for bootstrapTotalMs aggregate timing. */
  readonly #now: () => number
  /**
   * Current persistent boot-attempt counter value. Stamped on every
   * BootstrapStage* event payload so observers can spot crash-loop boots
   * (attempt 1 vs attempt 27 looks very different in the Timeline).
   * Defaults to 1 if the composition root didn't thread a real counter.
   */
  readonly #bootAttemptNumber: number
  /**
   * Callback invoked exactly once after the orchestrator completes every
   * stage successfully. Resets the persistent boot-retry counter so the
   * next cold boot starts at attempt 1 again.
   */
  readonly #onBootstrapSucceeded: (() => Promise<void> | void) | undefined
  /**
   * Shared in-memory collector for non-fatal boot issues raised by NON-CRITICAL
   * (spec) stages (self-healing-runtime-specs, card D1). The composition root
   * passes the SAME instance to D2's `get_boot_issues` MCP tool so the embedded
   * agent can read exactly what failed. Defaults to a fresh store when the
   * caller doesn't thread one (older tests).
   */
  readonly #bootIssues: BootIssueStore

  constructor(deps: {
    stages: readonly BootstrapStage[]
    signalr: SignalRClient
    config: DaemonConfig
    logger: Logger
    /**
     * Optional structured RuntimeEvent emitter (runtime-spec-v2 "Event taxonomy").
     * When provided, the orchestrator emits paired `BootstrapStageStarted` /
     * `BootstrapStageCompleted` / `BootstrapStageFailed` events for each stage
     * with mandatory timing (durationMs auto-filled via startTimer), and a
     * single `BootstrapStageCompleted` aggregate at end with `bootstrapTotalMs`
     * on the synthetic `__bootstrap__` stage name so the drawer can show
     * "Boot completed in 14.2s" at a glance.
     *
     * Left optional so older tests that wire the orchestrator without the
     * emitter keep working — the legacy `emitEvent` (AssistantText) path
     * fires unconditionally on top.
     */
    emitter?: RuntimeEventEmitter
    /** Monotonic clock for aggregate timing — defaults to `Date.now`. */
    now?: () => number
    /**
     * Current boot-attempt number (persistent across daemon respawns). Stamped
     * on every `BootstrapStage*` event payload as `bootAttemptNumber` so the
     * super-admin drawer can spot crash-looping daemons. Defaults to 1.
     */
    bootAttemptNumber?: number
    /**
     * Called exactly once after all stages complete successfully. Production
     * wires it to `BootRetryCounter.reset()` so the next cold boot starts
     * fresh. Failures are caught + logged inside the orchestrator — the reset
     * is best-effort and must not fail bootstrap.
     */
    onBootstrapSucceeded?: () => Promise<void> | void
    /**
     * Shared store for non-fatal boot issues (self-healing-runtime-specs, D1).
     * Production wires the same instance into D2's `get_boot_issues` MCP tool.
     * Defaults to a fresh store so older tests / call sites that don't thread
     * one keep working.
     */
    bootIssues?: BootIssueStore
  }) {
    this.#stages = deps.stages
    this.#signalr = deps.signalr
    this.#config = deps.config
    this.#logger = deps.logger.child({ module: 'bootstrap' })
    this.#emitter = deps.emitter
    this.#now = deps.now ?? Date.now
    this.#bootAttemptNumber = deps.bootAttemptNumber ?? 1
    this.#onBootstrapSucceeded = deps.onBootstrapSucceeded
    this.#bootIssues = deps.bootIssues ?? new BootIssueStore()
  }

  /**
   * Runs every stage in order. Returns when all stages succeed; throws
   * `BootstrapAbortedError` on unrecoverable failure or exhausted retries.
   *
   * AbortSignal semantics: if the signal is aborted *between* stages we return
   * cleanly without throwing — the supervisor decided to shut down, this is not
   * a bootstrap failure. If the signal aborts *during* a backoff wait or right
   * before a stage runs, we throw `BootstrapAbortedError` with reason
   * `'aborted'` — this is treated as a stage-level abort because we never got
   * to verify whether the stage succeeded.
   */
  async start(signal: AbortSignal): Promise<void> {
    // Hard ceiling on the persistent across-respawns counter. The orchestrator
    // legitimately honours `recoverable: false` in-process (throws below) and
    // caps in-process recoverable retries at MAX_ATTEMPTS — but supervisord's
    // `autorestart=true` happily respawns the daemon and the counter climbs
    // each time. Without this check a stage that's hosed for an environmental
    // reason (e.g. the smoketest-mongo case at attempt 104) loops forever.
    //
    // Terminal: the .NET side (ScheduleRespawnHandler) is responsible from
    // here. main.ts surfaces the failure with `sendErrorReport` + exit(1) so
    // the audit trail lands and the heartbeat watcher's Crashed transition
    // fires once the daemon stays silent past its threshold.
    if (this.#bootAttemptNumber > MAX_BOOT_ATTEMPTS) {
      this.#logger.error(
        { bootAttemptNumber: this.#bootAttemptNumber, cap: MAX_BOOT_ATTEMPTS },
        'bootstrap boot-attempt cap exceeded; refusing to start',
      )
      throw new BootstrapAbortedError({
        stage: '__bootstrap__',
        reason: `boot-attempt cap exceeded (${this.#bootAttemptNumber} > ${MAX_BOOT_ATTEMPTS})`,
        attempts: this.#bootAttemptNumber,
        terminal: true,
      })
    }

    const bootstrapStartMs = this.#now()
    let specHealthReported = false
    for (const stage of this.#stages) {
      if (signal.aborted) {
        // Aborted between stages — clean shutdown, not a failure.
        this.#logger.info({ stage: stage.name }, 'bootstrap aborted before stage start')
        return
      }

      // Report spec health to the backend just BEFORE the (critical) ReportReady
      // stage flips the runtime Online. By this point every NON-CRITICAL spec
      // stage has run and recorded any BootIssues, so `specHealth` is final.
      // Guard with a flag so a spec that (somehow) has two report-ready stages
      // only reports once. If there's no report-ready stage at all (legacy
      // tests), we fall back to reporting after the loop below.
      if (this.#isReportReady(stage) && !specHealthReported) {
        await this.#finalizeSpecHealth()
        specHealthReported = true
      }

      if (this.#isCritical(stage)) {
        // CRITICAL stage — legacy fatal behaviour: retry with backoff, abort on
        // unrecoverable / exhausted failure.
        await this.#runStageWithRetries(stage, signal)
      } else {
        // NON-CRITICAL (spec) stage — fail fast on deterministic spec bugs.
        // Records a BootIssue + continues; never aborts the boot.
        await this.#runSpecStage(stage, signal)
      }
    }

    // Fallback: no report-ready stage ran (e.g. a stage list without one, as in
    // some unit tests). Still surface the final spec health so the degraded
    // path is observable.
    if (!specHealthReported) {
      await this.#finalizeSpecHealth()
    }
    await this.#emitRuntimeEvent('bootstrap_completed', {})
    // Aggregate timing — single event keyed by the synthetic `__bootstrap__`
    // stage name so the drawer can show "Boot completed in 14.2s" without
    // joining per-stage events. We deliberately fire this AFTER every stage
    // has completed (and after the legacy bootstrap_completed event above)
    // so observers see stages-first, total-second order.
    if (this.#emitter !== undefined) {
      const bootstrapTotalMs = Math.max(0, this.#now() - bootstrapStartMs)
      this.#emitter.emit(
        RuntimeEventTypes.BootstrapStageCompleted,
        'Info',
        {
          stageName: '__bootstrap__',
          bootstrapTotalMs,
          durationMs: bootstrapTotalMs,
          bootAttemptNumber: this.#bootAttemptNumber,
        },
      )
    }
    // Reset the persistent boot-retry counter so the next cold boot starts at
    // attempt 1. Best-effort: a write failure here just means the next boot
    // reports an inflated attempt number. Never throw — bootstrap succeeded.
    if (this.#onBootstrapSucceeded !== undefined) {
      try {
        await this.#onBootstrapSucceeded()
      } catch (err) {
        this.#logger.warn({ err }, 'onBootstrapSucceeded callback failed')
      }
    }
    this.#logger.info('bootstrap completed')
  }

  async #runStageWithRetries(stage: BootstrapStage, signal: AbortSignal): Promise<void> {
    let lastReason = 'unknown'
    for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
      if (signal.aborted) {
        throw new BootstrapAbortedError({
          stage: stage.name,
          reason: 'aborted',
          attempts: attempt - 1,
        })
      }

      const ctx: BootstrapContext = {
        config: this.#config,
        signalr: this.#signalr,
        logger: this.#logger.child({ stage: stage.name, attempt }),
        signal,
      }

      // Paired BootstrapStageStarted / Completed / Failed events with
      // mandatory timing. Started fires before stage.run; the matching end
      // event auto-fills durationMs via the timer surface. `bootAttemptNumber`
      // is the persistent across-respawns counter — different from `attempt`,
      // which is this-process retry budget.
      const timer = this.#emitter?.startTimer(
        RuntimeEventTypes.BootstrapStageStarted,
        {
          stageName: stage.name,
          attempt,
          bootAttemptNumber: this.#bootAttemptNumber,
        },
      )

      let result: BootstrapStageResult
      try {
        result = await stage.run(ctx)
      } catch (err) {
        // A throwing stage is treated as a recoverable failure. Stages that
        // know they're hosed should return `{ ok: false, recoverable: false }`
        // rather than throw — this branch is a defensive backstop.
        const reason = err instanceof Error ? err.message : String(err)
        result = { ok: false, reason: `threw: ${reason}`, recoverable: true }
      }

      await this.#emitRuntimeEvent('bootstrap_stage_completed', {
        stage: stage.name,
        attempt,
        ok: result.ok,
        bootAttemptNumber: this.#bootAttemptNumber,
        ...(result.ok ? {} : { reason: result.reason, recoverable: result.recoverable }),
      })

      if (result.ok) {
        timer?.complete(RuntimeEventTypes.BootstrapStageCompleted, {
          stageName: stage.name,
          attempt,
          bootAttemptNumber: this.#bootAttemptNumber,
        })
        return
      }

      timer?.fail(RuntimeEventTypes.BootstrapStageFailed, {
        stageName: stage.name,
        attempt,
        errorMessage: result.reason,
        recoverable: result.recoverable,
        bootAttemptNumber: this.#bootAttemptNumber,
      })

      lastReason = result.reason

      if (!result.recoverable) {
        // Stage knows it's hosed — no retry, terminal failure. The .NET
        // backend's ScheduleRespawnHandler is responsible from here; main.ts
        // surfaces this via `sendErrorReport` + exit(1).
        throw new BootstrapAbortedError({
          stage: stage.name,
          reason: result.reason,
          attempts: attempt,
          terminal: true,
        })
      }

      if (attempt < MAX_ATTEMPTS) {
        const wait = BACKOFF_MS[attempt - 1] ?? 30_000
        this.#logger.warn(
          { stage: stage.name, attempt, reason: result.reason, retryInMs: wait },
          'bootstrap stage failed (recoverable); retrying',
        )
        const aborted = await waitWithAbort(wait, signal)
        if (aborted) {
          throw new BootstrapAbortedError({
            stage: stage.name,
            reason: 'aborted',
            attempts: attempt,
          })
        }
      }
    }

    throw new BootstrapAbortedError({
      stage: stage.name,
      reason: lastReason,
      attempts: MAX_ATTEMPTS,
    })
  }

  /**
   * A stage is CRITICAL unless it explicitly sets `critical = false`. Default
   * (undefined) preserves the legacy fatal behaviour so every pre-D1 stage and
   * test keeps aborting the boot on failure.
   */
  #isCritical(stage: BootstrapStage): boolean {
    return stage.critical !== false
  }

  /** True for the final `report-ready` stage (matched by its stable name). */
  #isReportReady(stage: BootstrapStage): boolean {
    return stage.name === 'report-ready'
  }

  /**
   * Run a NON-CRITICAL (spec) stage with FAIL-FAST semantics
   * (self-healing-runtime-specs, card D1).
   *
   * The contract: a deterministic spec failure (bad install script, a service
   * that never binds, setup bash that errors) must NOT abort the boot — we
   * record a `BootIssue` and CONTINUE so `ReportReady` still flips the runtime
   * Online (degraded). This is the whole point of decoupling "alive" from
   * "spec-applied".
   *
   * === Transient vs deterministic (the card's refinement) ===
   *
   * We still want to absorb genuine infra flakes (an apt mirror blip, a bundle
   * fetch that 503s, a registry timeout) WITHOUT looping 5× on a deterministic
   * spec bug. The pragmatic split chosen here:
   *
   *   - Run the stage once.
   *   - On failure, if the failure reason matches the TRANSIENT-infra allowlist
   *     (`#isTransientReason`) AND the stage reported it as `recoverable`, give
   *     it a small, bounded retry budget (`MAX_SPEC_STAGE_RETRIES`) with the
   *     same backoff schedule. A real flake usually clears within a couple of
   *     attempts.
   *   - Any other failure — or exhausting the transient budget — is treated as
   *     DETERMINISTIC: record one BootIssue and move on. No further retries.
   *
   * Rationale for the heuristic (documented per the card): the stage `result`
   * objects only carry `{ reason, recoverable }` today, and `recoverable` is set
   * `true` by every spec stage's catch block (install/setup/services all return
   * `recoverable: true` on any failure, because at authoring time they couldn't
   * tell a flake from a bug). So `recoverable` alone can't distinguish the two.
   * Matching the human-readable reason against a small allowlist of known infra-
   * flake signatures is the cheapest reliable discriminator without reworking
   * every stage's result shape. False negatives (a flake we don't recognise) are
   * cheap — worst case the runtime comes up Degraded and the agent re-applies;
   * false positives (a deterministic bug we retry a couple of times) cost a few
   * seconds, bounded by `MAX_SPEC_STAGE_RETRIES`.
   */
  async #runSpecStage(stage: BootstrapStage, signal: AbortSignal): Promise<void> {
    for (let attempt = 1; attempt <= MAX_SPEC_STAGE_RETRIES + 1; attempt++) {
      if (signal.aborted) {
        // Aborted mid spec-stage retry loop — treat as a clean shutdown signal,
        // not a spec failure. Don't record a BootIssue for an operator-initiated
        // abort. The outer loop already returns on `signal.aborted`.
        this.#logger.info(
          { stage: stage.name, attempt },
          'spec stage aborted (clean shutdown); not recording boot issue',
        )
        return
      }

      const ctx: BootstrapContext = {
        config: this.#config,
        signalr: this.#signalr,
        logger: this.#logger.child({ stage: stage.name, attempt, critical: false }),
        signal,
      }

      // Paired Started / Completed / Failed timing events, identical to the
      // critical path so the Timeline tab renders spec stages the same way.
      const timer = this.#emitter?.startTimer(RuntimeEventTypes.BootstrapStageStarted, {
        stageName: stage.name,
        attempt,
        bootAttemptNumber: this.#bootAttemptNumber,
        critical: false,
      })

      let result: BootstrapStageResult
      try {
        result = await stage.run(ctx)
      } catch (err) {
        const reason = err instanceof Error ? err.message : String(err)
        // A throwing spec stage is treated like any other failure result. We
        // mark it recoverable so the transient allowlist below still gets a
        // chance to absorb a throw that happens to be a network blip.
        result = { ok: false, reason: `threw: ${reason}`, recoverable: true }
      }

      await this.#emitRuntimeEvent('bootstrap_stage_completed', {
        stage: stage.name,
        attempt,
        ok: result.ok,
        critical: false,
        bootAttemptNumber: this.#bootAttemptNumber,
        ...(result.ok ? {} : { reason: result.reason, recoverable: result.recoverable }),
      })

      if (result.ok) {
        timer?.complete(RuntimeEventTypes.BootstrapStageCompleted, {
          stageName: stage.name,
          attempt,
          bootAttemptNumber: this.#bootAttemptNumber,
          critical: false,
        })
        return
      }

      timer?.fail(RuntimeEventTypes.BootstrapStageFailed, {
        stageName: stage.name,
        attempt,
        errorMessage: result.reason,
        recoverable: result.recoverable,
        bootAttemptNumber: this.#bootAttemptNumber,
        critical: false,
      })

      const transient = result.recoverable && this.#isTransientReason(result.reason)
      const hasBudgetLeft = attempt <= MAX_SPEC_STAGE_RETRIES
      if (transient && hasBudgetLeft) {
        const wait = BACKOFF_MS[attempt - 1] ?? 30_000
        this.#logger.warn(
          { stage: stage.name, attempt, reason: result.reason, retryInMs: wait },
          'spec stage failed (looks transient); retrying within bounded budget',
        )
        const aborted = await waitWithAbort(wait, signal)
        if (aborted) {
          this.#logger.info(
            { stage: stage.name, attempt },
            'spec stage retry aborted (clean shutdown); not recording boot issue',
          )
          return
        }
        continue
      }

      // Deterministic spec failure (or transient budget exhausted). Record a
      // BootIssue and CONTINUE — never abort the boot. Only attach a `detail`
      // when the failure looked transient but outlasted the retry budget —
      // omit it entirely otherwise (exactOptionalPropertyTypes forbids an
      // explicit `undefined` on the optional field).
      const retryLabel = MAX_SPEC_STAGE_RETRIES === 1 ? 'retry' : 'retries'
      this.#bootIssues.record({
        stage: stage.name,
        reason: result.reason,
        ...(transient
          ? {
              detail: `transient-looking failure persisted past ${MAX_SPEC_STAGE_RETRIES} ${retryLabel}`,
            }
          : {}),
      })
      this.#logger.warn(
        { stage: stage.name, reason: result.reason, attempt },
        'spec stage failed deterministically; recorded boot issue and continuing (degraded)',
      )
      return
    }
  }

  /**
   * Heuristic discriminator for TRANSIENT infra flakes vs DETERMINISTIC spec
   * bugs. Matches the failure reason against a small allowlist of known
   * infra-flake signatures (network, DNS, apt/registry mirrors, timeouts). See
   * `#runSpecStage` for the full rationale on why a reason-string match is the
   * pragmatic discriminator here.
   *
   * Conservative by design: anything we don't positively recognise as a flake
   * is treated as deterministic (record + continue). That's the safe default —
   * the runtime comes Online degraded and the agent self-heals, rather than
   * burning the boot retrying a real bug.
   */
  #isTransientReason(reason: string): boolean {
    const r = reason.toLowerCase()
    return TRANSIENT_REASON_PATTERNS.some((p) => p.test(r))
  }

  /**
   * Compute final SpecHealth from the boot-issue store, report it to the
   * backend, and emit one `SpecDegraded` RuntimeEvent per issue. Best-effort:
   * a transport failure here NEVER fails the boot (the runtime reaches Online
   * via `ReportReady` regardless). Idempotent-ish — the caller guards against
   * double invocation, but a second call would just re-report the same state.
   */
  async #finalizeSpecHealth(): Promise<void> {
    const issues: BootIssue[] = this.#bootIssues.list()
    const health: 'Healthy' | 'Degraded' = issues.length > 0 ? 'Degraded' : 'Healthy'

    // One SpecDegraded (Warn) event per issue so the Timeline shows each failed
    // spec stage individually. Only emitted on the degraded path.
    if (this.#emitter !== undefined) {
      for (const issue of issues) {
        this.#emitter.emit(RuntimeEventTypes.SpecDegraded, 'Warn', {
          stage: issue.stage,
          ...(issue.service !== undefined ? { service: issue.service } : {}),
          reason: issue.reason,
          ...(issue.detail !== undefined ? { detail: issue.detail } : {}),
          occurredAt: issue.occurredAt,
        })
      }
    }

    const summary =
      health === 'Healthy'
        ? 'Runtime spec applied cleanly.'
        : `Runtime started but the spec didn't fully apply — ${issues.length} issue${issues.length === 1 ? '' : 's'} across ${[...new Set(issues.map((i) => i.stage))].join(', ')}.`

    this.#logger.info({ health, issueCount: issues.length }, 'reporting spec health')

    // Best-effort: reporting spec health must NEVER fail the boot. The runtime
    // reaches Online via the (critical) ReportReady stage regardless of whether
    // this report lands. SignalRClient.reportSpecHealth is itself best-effort
    // (swallows + logs), but we still defend against a missing method (older /
    // partial signalr stubs in tests) and any synchronous throw here.
    try {
      await this.#signalr.reportSpecHealth?.({
        health,
        issues: issues as unknown as ReadonlyArray<Record<string, unknown>>,
        summary,
      })
    } catch (err) {
      this.#logger.warn(
        { err, health, issueCount: issues.length },
        'reportSpecHealth failed; continuing (runtime still reaches Online)',
      )
    }
  }

  /**
   * Best-effort runtime-scope event emit. Failures here NEVER fail bootstrap —
   * the orchestrator's job is to bring the runtime up; status-event delivery
   * is a side-channel for operators.
   *
   * Runtime-scope events use `kind: Status` as a routing carrier (sessionId
   * is empty, hub routes by runtimeId from the connection). The real
   * bootstrap subtype lives in `eventData` JSON for the audit row. Replace
   * once the runtime-bootstrap spec adds a dedicated hub method
   * (e.g. `EmitRuntimeEvent(payload)`).
   */
  async #emitRuntimeEvent(type: string, body: Record<string, unknown>): Promise<void> {
    const payload: EmitEventPayload = {
      sessionId: '',
      kind: AgentEventKind.Status,
      eventData: JSON.stringify({ type, ...body }),
      emittedAt: new Date().toISOString(),
    }
    try {
      await this.#signalr.emitEvent(payload)
    } catch (err) {
      this.#logger.error({ err, type }, 'failed to emit bootstrap event')
      // Swallow: a failed status event must not abort bootstrap.
    }
  }
}

/**
 * Sleep for `ms` or until `signal` aborts. Returns `true` if aborted, `false`
 * if the wait completed.
 *
 * We deliberately use the global `setTimeout` (not `node:timers/promises`)
 * because vitest's fake timers patch the global by default but do not patch
 * `node:timers/promises.setTimeout` — keeping the implementation timing
 * deterministically testable.
 */
function waitWithAbort(ms: number, signal: AbortSignal): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    if (signal.aborted) {
      resolve(true)
      return
    }
    const onAbort = () => {
      clearTimeout(timer)
      resolve(true)
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve(false)
    }, ms)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}
