// StartingServicesStage — registers + starts every service declared in the
// V2 runtime spec (`payload.runtimeSpec.services`).
//
// === V2 contract ===
//
// The wire payload (`BootstrapPayloadV2.runtimeSpec.services`) is an array of
// full `ServiceSpec` objects: name + command + optional user/env/autorestart/
// healthcheck/install. We pass each spec verbatim to
// `SupervisordController.addService`, which renders a supervisord program
// block from the object and runs `supervisorctl reread && supervisorctl
// update` to register + start it.
//
// === Healthcheck wait ===
//
// After `addService` returns (supervisord has registered + told the program
// to start), we poll `supervisorctl status` until each service is in RUNNING
// (or until 60s elapses). This is the closest thing supervisord exposes to a
// startup-completed signal.
//
// If a service declares a `healthcheck.command`, we ALSO run that command
// (with the spec's `intervalSeconds` between attempts, default 5s) and
// require exit 0 before declaring the service ready. The supervisorctl-status
// poll alone only tells us the process is alive, not that it's responsive —
// the active healthcheck catches the "Postgres bound the socket but isn't
// accepting connections yet" class of races.

import { spawn as nodeSpawn, type ChildProcessByStdio } from 'node:child_process'
import type { Readable } from 'node:stream'

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
import type {
  ServiceSpec,
  SupervisordController,
} from '../../runtime/SupervisordController.js'
import type { BootstrapState } from '../BootstrapState.js'
import { BootstrapOutputBatcher } from '../BootstrapOutputBatcher.js'
import type { BootIssueStore } from '../BootIssueStore.js'

const DEFAULT_HEALTH_TIMEOUT_MS = 180_000
const HEALTH_POLL_INTERVAL_MS = 1_000
const DEFAULT_HEALTHCHECK_INTERVAL_SECONDS = 5

/**
 * Fast-fail thresholds (self-healing-runtime-specs, card D1).
 *
 * Supervisord's FATAL state is terminal: it means the program exhausted its
 * `startretries` and supervisord has GIVEN UP — it will never transition back
 * to RUNNING on its own. There's no value in waiting the full health deadline
 * for it, so a single observed FATAL fast-fails that one service (recorded as a
 * BootIssue) instead of burning ~180s.
 *
 * EXITED is softer — a service could legitimately exit-0 then be restarted, or
 * flap. We require it to PERSIST across this many consecutive polls before
 * fast-failing, so a brief startup blip doesn't trip it.
 */
const EXITED_FASTFAIL_POLLS = 5

/**
 * Narrow structural shape of a `requiredEnv` entry on a `ServiceSpec`. The
 * generated wire `ServiceSpec` type doesn't yet declare `requiredEnv` (the
 * signalr codegen is currently blocked on an unrelated V3 preset type that
 * uses `JsonElement`), but the backend DOES serialize
 * `ServiceSpec.RequiredEnv` onto the wire — so the data is present at runtime.
 * We read it through this structural type until codegen is unblocked, at which
 * point this can be replaced by the generated field.
 */
interface RequiredEnvDecl {
  key: string
  description?: string
  secret?: boolean
}

/**
 * Pull the (possibly absent) `requiredEnv` declarations off a service spec.
 * Returns `[]` when the field is missing or malformed so callers can iterate
 * unconditionally.
 */
function readRequiredEnv(spec: unknown): RequiredEnvDecl[] {
  const raw = (spec as { requiredEnv?: unknown }).requiredEnv
  if (!Array.isArray(raw)) return []
  return raw.filter(
    (e): e is RequiredEnvDecl =>
      typeof e === 'object' &&
      e !== null &&
      typeof (e as { key?: unknown }).key === 'string',
  )
}

/**
 * Cap per stream (stdout/stderr) on the rolling probe-result tail we surface
 * in the {@link RuntimeEventTypes.ServiceHealthcheckTimedOut} payload. Probes
 * are short by nature (curl, pg_isready), but a misbehaving probe that prints
 * megabytes shouldn't be allowed to bloat the wire envelope.
 */
const PROBE_TAIL_MAX_BYTES = 2 * 1024

/**
 * Bucketed-emission cadence for {@link RuntimeEventTypes.ServiceHealthcheckProbeFailed}.
 * A 180s deadline at the default 5s interval would yield 36 failed probes per
 * service; emitting all of them floods the events table with low-signal
 * rows. We emit only on exit-code transitions OR every Nth attempt as a
 * lower-bound liveness signal.
 */
const PROBE_FAILED_EMIT_EVERY = 5

/**
 * Supervisord renders every program's stdout+stderr (merged) at this path.
 * See SupervisordController.renderServiceBlock — kept in lockstep.
 */
const SUPERVISOR_LOG_DIR = '/var/log/supervisor'

/**
 * How many tail lines to grab from a stuck service's log when we emit a
 * ServiceCrashed / ServiceFailedToStart diagnostic. Big enough to hold a
 * typical .NET / Node stack trace; small enough to stay under the 32KB
 * SignalR transport cap once the event envelope is added.
 */
const SERVICE_LOG_TAIL_LINES = 50

export interface StartingServicesStageDeps {
  signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  state: BootstrapState
  supervisord: Pick<
    SupervisordController,
    'addService' | 'reconcileServices' | 'listConfiguredServiceNames'
  >
  executor: IExecutor
  /**
   * Structured RuntimeEvent emitter. When provided, the stage emits
   * ServiceStarting (before supervisord addService) and ServiceRunning
   * (when supervisorctl reports RUNNING) per service with durationMs
   * measured from the ServiceStarting timestamp. Optional for back-compat
   * with older tests.
   */
  emitter?: RuntimeEventEmitter
  /**
   * Read-only view of the runtime's env-var snapshot, used by the pre-flight
   * required-env guard: before registering a service with supervisord we check
   * every `requiredEnv` key declared on its spec is present (and non-empty) in
   * this snapshot. A service missing required vars is skipped (not started) and
   * a `ServiceEnvMissing` event is emitted, so a misconfigured service degrades
   * the runtime instead of crash-looping it. Optional for back-compat with
   * older tests; when omitted the guard is disabled and every service is
   * registered as before.
   */
  envVarManager?: { current(): ReadonlyMap<string, string> }
  /**
   * Shared in-memory boot-issue collector (self-healing-runtime-specs, card
   * D1). When provided, a service that fails to start (FATAL/EXITED persistently
   * or never reaches RUNNING within the deadline) is recorded as a per-service
   * `BootIssue` and the stage STILL returns ok — a wedged service degrades the
   * runtime instead of failing the whole boot. When omitted (older tests), the
   * stage keeps the legacy behaviour of returning a failure result for the
   * whole stage. The orchestrator records a single stage-level BootIssue in
   * that fallback case, so the degraded-online guarantee holds either way; the
   * store just lets us record richer per-service granularity.
   */
  bootIssues?: BootIssueStore
  /** Override healthcheck timeout for tests (default 180s). */
  healthTimeoutMs?: number
  /** Override healthcheck poll interval for tests (default 1s). */
  healthPollIntervalMs?: number
  /** Monotonic clock for durationMs accounting. Defaults to `Date.now`. */
  now?: () => number
  /**
   * Override the child-spawn used to launch per-service `tail -F` for
   * `ServiceOutputChunk` capture. Defaults to `node:child_process.spawn`.
   * Tests inject a fake to avoid actually shelling out — the bootstrap-time
   * unit tests don't have a real log directory either.
   */
  spawn?: typeof nodeSpawn
}

export class StartingServicesStage implements BootstrapStage {
  readonly name = 'starting-services'
  // NON-CRITICAL (spec) stage: a service that won't start becomes a BootIssue,
  // not a fatal boot failure. The orchestrator records it + continues so the
  // runtime reaches Online (degraded). See self-healing-runtime-specs card D1.
  readonly critical = false

  readonly #signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #supervisord: Pick<
    SupervisordController,
    'addService' | 'reconcileServices' | 'listConfiguredServiceNames'
  >
  readonly #executor: IExecutor
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #envVarManager: { current(): ReadonlyMap<string, string> } | undefined
  readonly #bootIssues: BootIssueStore | undefined
  readonly #healthTimeoutMs: number
  readonly #healthPollIntervalMs: number
  readonly #now: () => number
  readonly #spawn: typeof nodeSpawn

  constructor(deps: StartingServicesStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#supervisord = deps.supervisord
    this.#executor = deps.executor
    this.#emitter = deps.emitter
    this.#envVarManager = deps.envVarManager
    this.#bootIssues = deps.bootIssues
    this.#healthTimeoutMs = deps.healthTimeoutMs ?? DEFAULT_HEALTH_TIMEOUT_MS
    this.#healthPollIntervalMs = deps.healthPollIntervalMs ?? HEALTH_POLL_INTERVAL_MS
    this.#now = deps.now ?? Date.now
    this.#spawn = deps.spawn ?? nodeSpawn
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    const services = this.#state.payload.runtimeSpec.services ?? []

    // ---- Reconcile orphan supervisord confs ----
    //
    // Self-heal step: the persistent volume can carry `.conf` files from a
    // previous spec revision (e.g. spec used to include postgres, was
    // edited to remove it, but the conf survived). Supervisord re-reads
    // every conf on reload and crash-loops on services whose binaries are
    // missing — that's the original Phase-1 bug we hit in production.
    //
    // Compute the desired-name set from the current spec and ask the
    // controller to drop any orphan confs (stop + remove + unlink). Data
    // directories under `/data/project/services/<name>/` are preserved.
    //
    // We do this BEFORE the addService loop so the supervisord-status poll
    // below isn't observing FATAL states for services we're about to
    // delete anyway. The reconcile is best-effort; a failure here logs but
    // doesn't fail the stage (the addService loop is what actually has to
    // succeed for boot to be considered OK).
    try {
      const desired = new Set(services.map((s) => s.name))
      const removed = await this.#supervisord.reconcileServices(desired)
      if (removed.length > 0) {
        ctx.logger.info(
          { removed, desired: [...desired] },
          'reconcileServices: removed orphan supervisord confs',
        )
        void this.#emit(
          ctx,
          'progress',
          `reconciled: removed ${removed.length} orphan conf(s): ${removed.join(', ')}`,
        )
      }
    } catch (err) {
      ctx.logger.warn(
        { err },
        'reconcileServices: failed (continuing — addService loop is the source of truth)',
      )
    }

    if (services.length === 0) {
      ctx.logger.info('no supervised services in spec — skipping')
      void this.#emit(ctx, 'skipped', 'no services')
      return { ok: true }
    }

    void this.#emit(ctx, 'started', `${services.length} service(s)`)

    // ServiceStarting → ServiceRunning duration tracking. Each service gets a
    // start timestamp recorded BEFORE we hand the spec to supervisord; when
    // supervisorctl reports RUNNING below we compute durationMs ourselves.
    // We can't use `emitter.startTimer` here because the start/end pair
    // straddles the async poll loop — the spec calls this out explicitly.
    const serviceStartTimes = new Map<string, number>()

    // Pre-flight required-env guard bookkeeping. Services whose declared
    // `requiredEnv` keys are missing/empty in the env snapshot are skipped
    // (never handed to supervisord) and recorded here so they're excluded from
    // the supervisord-status wait below — otherwise we'd wait the full health
    // deadline for a service we deliberately never started.
    const envSkipped = new Set<string>()

    // Per-service stdout/stderr tail state. Populated after `addService`
    // returns and torn down in the `finally` below — explicitly ONLY tailing
    // during the starting-services window. Post-Online live-tail is handled
    // by `LogTailer` (the SignalR-driven on-demand path), not us.
    const serviceTails: ServiceTailHandle[] = []

    try {
      for (const spec of services) {
        if (ctx.signal.aborted) {
          return { ok: false, reason: 'aborted', recoverable: true }
        }

        // ---- Pre-flight required-env guard (layer-2 hardening) ----
        //
        // Before asking supervisord to start this service, verify every
        // `requiredEnv` key it declares is present (and non-empty) in the
        // runtime's env snapshot. A service that's missing a required secret
        // (e.g. OPENROUTER_API_KEY) would otherwise boot and immediately
        // crash-loop, burning the whole health deadline and flooding the
        // Timeline with ServiceCrashed events. Instead we SKIP registration,
        // emit a single ServiceEnvMissing event, and let the runtime come
        // Online degraded — the operator adds the secret in the Environment
        // tab and restarts just that service.
        //
        // The guard only runs when an envVarManager was injected; older tests
        // that don't wire one get the pre-Phase behaviour (every service
        // registered) so they keep passing.
        if (this.#envVarManager !== undefined) {
          const required = readRequiredEnv(spec)
          if (required.length > 0) {
            const env = this.#envVarManager.current()
            const missingEnvVars = required
              .map((r) => r.key)
              .filter((key) => {
                const value = env.get(key)
                return value === undefined || value === ''
              })
            if (missingEnvVars.length > 0) {
              ctx.logger.warn(
                { serviceName: spec.name, missingEnvVars },
                'starting-services: skipping service — required env vars missing',
              )
              this.#emitter?.emit(RuntimeEventTypes.ServiceEnvMissing, 'Warn', {
                serviceName: spec.name,
                missingEnvVars,
              })
              void this.#emit(
                ctx,
                'progress',
                `skip ${spec.name}: missing env ${missingEnvVars.join(', ')}`,
              )
              envSkipped.add(spec.name)
              continue
            }
          }
        }

        void this.#emit(ctx, 'progress', `register: ${spec.name}`)
        // Record start moment + emit ServiceStarting BEFORE the supervisord
        // addService call so the timing reflects "user asked for this service
        // to start", not "supervisord finished writing the conf".
        serviceStartTimes.set(spec.name, this.#now())
        this.#emitter?.emit(RuntimeEventTypes.ServiceStarting, 'Info', {
          serviceName: spec.name,
        })
        try {
          // The generated ServiceSpec from the wire has `env` typed as
          // `Partial<Record<string, string>>` (because tsrts loosens Dictionary
          // values). The SupervisordController.ServiceSpec uses the simpler
          // `Record<string, string>`; the env values are non-undefined at
          // runtime (the C# Validate doesn't allow null values into the dict),
          // so we cast the entire spec at the boundary.
          await this.#supervisord.addService(spec as ServiceSpec, ctx.signal)
        } catch (err) {
          const reason = err instanceof Error ? err.message : String(err)
          void this.#emit(ctx, 'failed', `${spec.name}: ${reason}`)
          return {
            ok: false,
            reason: `addService(${spec.name}) failed: ${reason}`,
            recoverable: true,
          }
        }

        // Wire up a stdout/stderr tail → `OutputLineBatcher` → ServiceOutputChunk
        // events so the operator sees what the service prints while we're
        // polling for RUNNING + running healthchecks. Best-effort: spawn
        // failures (`tail` missing, log path unwritable in tests) are logged
        // and skipped; the rest of the stage still runs.
        if (this.#emitter !== undefined) {
          const handle = this.#startServiceTail(ctx, spec.name, this.#emitter)
          if (handle !== undefined) serviceTails.push(handle)
        }
      }

      // Poll supervisorctl status until every requested service is RUNNING (or
      // we time out). supervisorctl exits 0 even when programs are FATAL, so we
      // parse the line shape `<name> <state> ...` ourselves.
      //
      // Per-service diagnostic state we accumulate during the wait loop:
      //
      //   - `lastEmittedState[name]`: dedupes ServiceCrashed emits so a service
      //     that flap-flops through BACKOFF every second doesn't spam the
      //     RuntimeEvents table. We emit on transition, not on every poll.
      //   - `crashCounts[name]`: best-effort count of distinct
      //     BACKOFF/FATAL/EXITED transitions, surfaced in the timeout payload.
      const deadline = Date.now() + this.#healthTimeoutMs
      const pendingSupervisor = new Set(
        services.map((s) => s.name).filter((name) => !envSkipped.has(name)),
      )
      const lastEmittedState = new Map<string, string>()
      const crashCounts = new Map<string, number>()
      // Fast-fail bookkeeping (self-healing-runtime-specs, card D1). Services we
      // give up on EARLY (before the deadline) because they're clearly wedged —
      // FATAL (supervisord exhausted startretries → terminal) or EXITED for
      // EXITED_FASTFAIL_POLLS consecutive polls. They move out of
      // `pendingSupervisor` into `fastFailed` so we don't hang the full ~180s on
      // a service that will never come up.
      const fastFailed = new Set<string>()
      const exitedStreak = new Map<string, number>()
      while (pendingSupervisor.size > 0 && Date.now() < deadline) {
        if (ctx.signal.aborted) {
          return { ok: false, reason: 'aborted', recoverable: true }
        }
        const states = await this.#readStates(ctx)
        for (const svcName of [...pendingSupervisor]) {
          const state = states.get(svcName)
          if (state === 'RUNNING') {
            pendingSupervisor.delete(svcName)
            lastEmittedState.set(svcName, 'RUNNING')
            exitedStreak.delete(svcName)
            // Compute durationMs from the ServiceStarting timestamp recorded
            // above. Fall back to 0 if (somehow) we lost the start time.
            const startMs = serviceStartTimes.get(svcName)
            const durationMs =
              startMs !== undefined ? Math.max(0, this.#now() - startMs) : 0
            this.#emitter?.emit(RuntimeEventTypes.ServiceRunning, 'Info', {
              serviceName: svcName,
              durationMs,
            })
            void this.#emit(ctx, 'progress', `${svcName} RUNNING`)
          } else if (state === 'FATAL' || state === 'EXITED' || state === 'BACKOFF') {
            // Don't immediately fail on BACKOFF — supervisord can transition
            // through it briefly during startup. BUT: emit a structured
            // ServiceCrashed event (with a log tail) the FIRST time we observe
            // each distinct bad state for this service, so Timeline shows the
            // operator what's happening BEFORE the deadline elapses. Subsequent
            // polls reporting the SAME state are deduped — the next transition
            // (FATAL → BACKOFF, etc.) re-arms the emit.
            if (lastEmittedState.get(svcName) !== state) {
              lastEmittedState.set(svcName, state)
              crashCounts.set(svcName, (crashCounts.get(svcName) ?? 0) + 1)
              // Best-effort log tail. Awaiting here lengthens the poll by ~30ms
              // per crashing service but the crashing-service path is already
              // off the happy path; richer diagnostics > a 30ms faster poll.
              const tail = await this.#tailServiceLog(ctx, svcName)
              this.#emitter?.emit(RuntimeEventTypes.ServiceCrashed, 'Warn', {
                serviceName: svcName,
                supervisorState: state,
                crashCount: crashCounts.get(svcName) ?? 1,
                logTailLines: tail,
              })
              void this.#emit(
                ctx,
                'progress',
                `${svcName} ${state} (tailing log; crash #${crashCounts.get(svcName) ?? 1})`,
              )
            }

            // ---- Fast-fail detection (card D1) ----
            // FATAL is terminal: supervisord exhausted startretries and will
            // never restart this program. Give up on it immediately rather than
            // burning the full deadline. EXITED is softer — require it to
            // persist EXITED_FASTFAIL_POLLS consecutive polls (a one-shot that
            // exits then gets restarted shouldn't trip it).
            let giveUp = false
            if (state === 'FATAL') {
              giveUp = true
            } else if (state === 'EXITED') {
              const streak = (exitedStreak.get(svcName) ?? 0) + 1
              exitedStreak.set(svcName, streak)
              if (streak >= EXITED_FASTFAIL_POLLS) giveUp = true
            } else {
              // BACKOFF (or anything else) resets the EXITED streak — the
              // service is still trying.
              exitedStreak.delete(svcName)
            }
            if (giveUp) {
              pendingSupervisor.delete(svcName)
              fastFailed.add(svcName)
              ctx.logger.warn(
                { serviceName: svcName, state },
                'starting-services: fast-failing wedged service (not waiting full deadline)',
              )
              void this.#emit(
                ctx,
                'progress',
                `${svcName} ${state} — fast-failing (will degrade, not block boot)`,
              )
            }
          }
        }
        if (pendingSupervisor.size === 0) break
        await sleepWithAbort(this.#healthPollIntervalMs, ctx.signal)
      }

      // Everything still pending at the deadline PLUS everything we fast-failed
      // is "stuck" — a service that never reached RUNNING.
      const stuckNames = [...new Set([...pendingSupervisor, ...fastFailed])]
      const stuckSet = new Set(stuckNames)
      if (stuckNames.length > 0) {
        const stuck = stuckNames
        // Capture rich diagnostics for every stuck service IN PARALLEL so the
        // operator's Timeline event carries the actual crash output. Without
        // this, all you get is "services did not reach RUNNING" — useless when
        // .NET's startup exception is what you actually need to see.
        const diagnostics = await Promise.all(
          stuck.map(async (name) => ({
            serviceName: name,
            lastState: lastEmittedState.get(name) ?? 'UNKNOWN',
            crashCount: crashCounts.get(name) ?? 0,
            logTailLines: await this.#tailServiceLog(ctx, name),
          })),
        )
        // Emit ONE event per stuck service so the Timeline shows them
        // individually (clearer than a single mega-event). Severity = Error
        // because this service genuinely failed to start.
        for (const diag of diagnostics) {
          this.#emitter?.emit(RuntimeEventTypes.ServiceFailedToStart, 'Error', {
            serviceName: diag.serviceName,
            timeoutMs: this.#healthTimeoutMs,
            lastSupervisorState: diag.lastState,
            crashCount: diag.crashCount,
            logTailLines: diag.logTailLines,
          })
        }
        // Build a compact human-readable reason that ALSO carries a few lines
        // of each tail. This string ends up either in a per-service BootIssue
        // (degraded path) or the whole-stage failure reason (legacy fallback).
        const tailPreview = diagnostics
          .map((d) => {
            const lastLines = d.logTailLines.slice(-5).join(' | ')
            return `${d.serviceName} (last=${d.lastState}, crashes=${d.crashCount})${lastLines.length > 0 ? `: ${lastLines}` : ''}`
          })
          .join(' ;; ')

        if (this.#bootIssues !== undefined) {
          // Degraded-online path (card D1): a wedged service does NOT fail the
          // whole boot. Record ONE BootIssue per stuck service (with its own
          // service name + log tail as detail) and CONTINUE — the services that
          // DID start get their healthchecks, then the stage returns ok so the
          // orchestrator marches on to ReportReady and the runtime reaches
          // Online (degraded).
          for (const diag of diagnostics) {
            const detailLines = diag.logTailLines.slice(-10).join('\n')
            // Only attach `detail` when there are actually tail lines —
            // exactOptionalPropertyTypes forbids an explicit `undefined` on the
            // optional field, so spread it in conditionally.
            this.#bootIssues.record({
              stage: this.name,
              service: diag.serviceName,
              reason: `service '${diag.serviceName}' did not reach RUNNING (last=${diag.lastState}, crashes=${diag.crashCount})`,
              ...(detailLines.length > 0 ? { detail: detailLines } : {}),
            })
          }
          void this.#emit(
            ctx,
            'progress',
            `degraded: ${stuckNames.length} service(s) failed to start — ${tailPreview}`,
          )
          ctx.logger.warn(
            { stuck: stuckNames },
            'starting-services: services failed to start; recorded boot issues and continuing (degraded)',
          )
          // fall through — do NOT return a failure result.
        } else {
          // Legacy fallback (no store wired, e.g. older tests): fail the whole
          // stage. The orchestrator still records a single stage-level BootIssue
          // for a non-critical stage, so the degraded-online guarantee holds.
          void this.#emit(ctx, 'failed', `services not running: ${tailPreview}`)
          return {
            ok: false,
            reason: `services did not reach RUNNING within ${this.#healthTimeoutMs}ms — ${tailPreview}`,
            recoverable: true,
          }
        }
      }

      // Active healthcheck pass (ADVISORY since runtime-spec-v2 healthcheck
      // softening). For any service with a `healthcheck.command`, run it on
      // the configured interval until exit-0 (or until the stage deadline).
      // The healthcheck used to be blocking — a missing /health endpoint
      // would hang bootstrap forever. Now: process-alive (supervisorctl
      // RUNNING above) is the hard gate; the active probe is best-effort
      // and never fails the stage. Timeline gets `ServiceHealthy` on success
      // or `ServiceHealthcheckTimedOut` (Warn, NOT Error) on deadline. Skip
      // silently when no healthcheck is declared.
      for (const spec of services) {
        if (spec.healthcheck === undefined) continue
        // Never healthcheck a service we skipped for missing env — it was never
        // registered with supervisord, so its probe would just time out.
        if (envSkipped.has(spec.name)) continue
        // Never healthcheck a service that failed to reach RUNNING (degraded
        // path): it's already a BootIssue, and its probe would just burn the
        // deadline again. Only the services that actually started get probed.
        if (stuckSet.has(spec.name)) continue
        if (ctx.signal.aborted) {
          return { ok: false, reason: 'aborted', recoverable: true }
        }
        // Same `env` cast as on the addService call above: generated env is
        // `Partial<Record<string, string>>`; runtime values are non-undefined.
        const probeStart = this.#now()
        const probeResult = await this.#runHealthcheck(
          ctx,
          spec as ServiceSpec,
          deadline,
        )
        if (probeResult.ok) {
          this.#emitter?.emit(RuntimeEventTypes.ServiceHealthy, 'Info', {
            serviceName: spec.name,
            durationMs: Math.max(0, this.#now() - probeStart),
            lastExitCode: 0,
          })
          void this.#emit(ctx, 'progress', `${spec.name} healthy`)
          continue
        }
        // Deadline expired without an exit-0. Emit a Warn — process is still
        // RUNNING, the runtime CAN proceed to Online, the probe is just not
        // confirming responsiveness. Caller can see the captured stdout/stderr
        // tail in the Timeline to figure out why.
        this.#emitter?.emit(
          RuntimeEventTypes.ServiceHealthcheckTimedOut,
          'Warn',
          {
            serviceName: spec.name,
            deadlineMs: this.#healthTimeoutMs,
            lastExitCode: probeResult.lastExitCode,
            lastStdoutTail: probeResult.lastStdoutTail,
            lastStderrTail: probeResult.lastStderrTail,
          },
        )
        void this.#emit(
          ctx,
          'progress',
          `${spec.name} healthcheck timed out (advisory; continuing)`,
        )
      }

      ctx.logger.info({ count: services.length }, 'all services running')
      void this.#emit(ctx, 'completed')
      return { ok: true }
    } finally {
      // Tear down every per-service tail we spawned above. `dispose()` flushes
      // the trailing partial chunk so the last few lines land on the Timeline
      // before the stage's BootstrapStageCompleted event. Idempotent — safe on
      // both ok and failure paths.
      for (const handle of serviceTails) {
        try {
          handle.dispose()
        } catch (err) {
          ctx.logger.debug(
            { err, service: handle.serviceName },
            'service tail dispose threw (best-effort)',
          )
        }
      }
    }
  }

  /**
   * Run the per-service healthcheck command on its interval until it exits 0
   * or we hit the overall stage deadline. Returns a structured result the
   * caller uses to decide whether to emit `ServiceHealthy` or
   * `ServiceHealthcheckTimedOut`. Each probe runs through the same IExecutor
   * as the rest of the stage; non-zero exits are tolerated (we keep polling
   * until either the deadline or a 0 exit).
   *
   * <p>Probe-level diagnostics: we capture stdout/stderr (capped at
   * ~2KB each) and carry the "last result" so the timeout payload can show
   * the operator what the most recent probe actually printed. Per-probe
   * `ServiceHealthcheckProbeFailed` events are emitted on a bucketed cadence
   * (exit-code transitions OR every {@link PROBE_FAILED_EMIT_EVERY}th attempt)
   * to avoid flooding the events table with 36 noisy rows per service.</p>
   */
  async #runHealthcheck(
    ctx: BootstrapContext,
    spec: ServiceSpec,
    deadline: number,
  ): Promise<HealthcheckResult> {
    const intervalMs =
      (spec.healthcheck?.intervalSeconds ?? DEFAULT_HEALTHCHECK_INTERVAL_SECONDS) * 1_000
    const command = spec.healthcheck?.command ?? ''
    if (command.length === 0) {
      return { ok: true, lastExitCode: 0, lastStdoutTail: '', lastStderrTail: '' }
    }

    let attemptCount = 0
    let previousExitCode: number | undefined
    let lastExitCode = -1
    let lastStdoutTail = ''
    let lastStderrTail = ''

    while (Date.now() < deadline) {
      if (ctx.signal.aborted) {
        return {
          ok: false,
          lastExitCode,
          lastStdoutTail,
          lastStderrTail,
        }
      }
      attemptCount += 1
      let exitCode: number | undefined
      let stdoutTail = ''
      let stderrTail = ''
      try {
        const result = await this.#executor.run('sh', ['-c', command], {
          allowNonZero: true,
        })
        exitCode = result.exitCode
        stdoutTail = capTailBytes(result.stdout, PROBE_TAIL_MAX_BYTES)
        stderrTail = capTailBytes(result.stderr, PROBE_TAIL_MAX_BYTES)
      } catch (err) {
        // Executor itself threw (spawn failed, timed out, bad shell). Surface
        // the error as a synthetic exit-code so the bucketed emission logic
        // still reads it as "non-zero" and the rolling tail carries the
        // failure message for the eventual timeout payload.
        const message = err instanceof Error ? err.message : String(err)
        exitCode = -1
        stderrTail = capTailBytes(message, PROBE_TAIL_MAX_BYTES)
        ctx.logger.debug(
          { err, service: spec.name },
          'healthcheck command threw; treating as failed probe',
        )
      }

      lastExitCode = exitCode
      lastStdoutTail = stdoutTail
      lastStderrTail = stderrTail

      if (exitCode === 0) {
        return {
          ok: true,
          lastExitCode: 0,
          lastStdoutTail,
          lastStderrTail,
        }
      }

      // Bucketed diagnostic emission: ALWAYS emit on exit-code transitions
      // (so we don't lose the moment "exit 7 → exit 1" happened), AND every
      // Nth attempt as a steady liveness signal. Skip everything in between
      // — 36 identical "exit 7" rows per stuck probe is just noise.
      const exitCodeChanged = previousExitCode !== exitCode
      const onCadence = attemptCount % PROBE_FAILED_EMIT_EVERY === 0
      if (exitCodeChanged || onCadence) {
        this.#emitter?.emit(
          RuntimeEventTypes.ServiceHealthcheckProbeFailed,
          'Info',
          {
            serviceName: spec.name,
            attemptCount,
            exitCode,
            stdoutTail,
            stderrTail,
            attemptedAt: new Date().toISOString(),
          },
        )
      }
      previousExitCode = exitCode
      void this.#emit(
        ctx,
        'progress',
        `${spec.name} healthcheck exit=${exitCode} (retrying)`,
      )

      await sleepWithAbort(intervalMs, ctx.signal)
    }
    return {
      ok: false,
      lastExitCode,
      lastStdoutTail,
      lastStderrTail,
    }
  }

  /**
   * Run `supervisorctl status` and parse the output into a name→state map.
   * Lines look like:
   *   redis                            RUNNING   pid 1234, uptime 0:00:05
   * We just take the first two whitespace-separated tokens of each line.
   */
  async #readStates(ctx: BootstrapContext): Promise<Map<string, string>> {
    const out = new Map<string, string>()
    try {
      const result = await this.#executor.run('supervisorctl', ['status'], {
        allowNonZero: true,
      })
      // `supervisorctl status` exits non-zero when at least one program is
      // STOPPED/FATAL/EXITED, so we always allow non-zero and parse stdout.
      for (const rawLine of result.stdout.split('\n')) {
        const line = rawLine.trim()
        if (line.length === 0) continue
        const parts = line.split(/\s+/)
        const name = parts[0]
        const state = parts[1]
        if (name !== undefined && state !== undefined) out.set(name, state)
      }
    } catch (err) {
      ctx.logger.debug({ err }, 'supervisorctl status failed; treating as no-info')
    }
    return out
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

  /**
   * Best-effort tail of a service's supervisord log file. Returns an empty
   * array on any failure (missing file, perms, empty log) so the caller can
   * include the field unconditionally without branching. Uses `tail -n` via
   * the executor for simplicity — the file is locally written and stdout-
   * redirected by supervisord, so we always have read access (same agent
   * user).
   *
   * <p>The path mirrors {@code SupervisordController.renderServiceBlock} —
   * <c>stdout_logfile=/var/log/supervisor/&lt;name&gt;.log</c> with stderr
   * redirected into the same file. If that path scheme ever changes, this
   * helper has to change in lockstep.</p>
   *
   * <p>Result lines are NOT prefixed with `[stdout]`/`[stderr]` (unlike
   * OutputTailBuffer) because supervisord merges them into a single file —
   * the prefix would be lying.</p>
   */
  async #tailServiceLog(
    ctx: BootstrapContext,
    serviceName: string,
  ): Promise<string[]> {
    const path = `${SUPERVISOR_LOG_DIR}/${serviceName}.log`
    try {
      const result = await this.#executor.run(
        'tail',
        ['-n', String(SERVICE_LOG_TAIL_LINES), path],
        { allowNonZero: true, timeoutMs: 5_000 },
      )
      if (result.exitCode !== 0) {
        // Exit 1 = file doesn't exist yet (service crashed too fast for
        // supervisord to open its stdout). Worth surfacing once.
        ctx.logger.debug(
          { serviceName, path, exitCode: result.exitCode, stderr: result.stderr },
          'tailServiceLog: non-zero exit (likely missing log file)',
        )
        return []
      }
      // Split on newline; drop trailing empties from the final `\n`.
      return result.stdout.split('\n').filter((line) => line.length > 0)
    } catch (err) {
      ctx.logger.debug(
        { err, serviceName, path },
        'tailServiceLog: tail invocation threw',
      )
      return []
    }
  }

  /**
   * Spawn a `tail -F -n 0` against the supervisord-managed log file for
   * `serviceName` and pipe each completed line into an
   * `OutputLineBatcher` configured to emit `ServiceOutputChunk` events. Lines
   * already-in-the-file are explicitly skipped (`-n 0`) — we only stream
   * what arrives AFTER the stage starts. The caller is responsible for
   * `dispose()`-ing the returned handle when the stage exits.
   *
   * <p>Best-effort: returns undefined on spawn failure (binary missing,
   * permission denied) so callers can ignore the optional. Tests inject a
   * fake `spawn` via the stage constructor.</p>
   *
   * <p>NOTE: supervisord renders services with `redirect_stderr=true` so the
   * single `.log` file already carries merged stdout+stderr. We emit chunks
   * with `stream: 'stdout'` to keep the payload shape symmetric with
   * `BootstrapOutputChunk` — the on-disk stream really is one channel here.
   * If the renderer ever stops redirecting, the `OutputLineBatcher` accepts
   * `addStderrLine` and we can split.</p>
   */
  #startServiceTail(
    ctx: BootstrapContext,
    serviceName: string,
    emitter: RuntimeEventEmitter,
  ): ServiceTailHandle | undefined {
    const path = `${SUPERVISOR_LOG_DIR}/${serviceName}.log`
    let child: ChildProcessByStdio<null, Readable, Readable>
    try {
      // `-n 0` ⇒ no replay. `-F` ⇒ re-open on rotation/truncation (supervisord
      // rotates at 10 MB). stdin ignored; stdout + stderr piped so we can
      // observe both even though we only forward stdout (stderr from `tail`
      // itself is informational — "file appeared", etc.).
      child = this.#spawn(
        'tail',
        ['-n', '0', '-F', path],
        { stdio: ['ignore', 'pipe', 'pipe'] },
      ) as ChildProcessByStdio<null, Readable, Readable>
    } catch (err) {
      ctx.logger.debug(
        { err, serviceName, path },
        'startServiceTail: spawn failed (continuing without per-service chunks)',
      )
      return undefined
    }

    const batcher = new BootstrapOutputBatcher({
      emitter,
      eventType: RuntimeEventTypes.ServiceOutputChunk,
      extraPayload: { serviceName },
    })

    let stdoutPartial = ''
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', (chunk: string) => {
      const combined = stdoutPartial + chunk
      const lines = combined.split('\n')
      stdoutPartial = lines.pop() ?? ''
      for (const line of lines) {
        batcher.addStdoutLine(line)
      }
    })
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', (chunk: string) => {
      // `tail -F` writes diagnostic notices to its own stderr ("file
      // appeared", "file truncated"). Surface at debug — emitting these as
      // ServiceOutputChunk(stream=stderr) would mislead the operator into
      // thinking the SERVICE printed them.
      ctx.logger.debug({ serviceName, chunk: chunk.trim() }, 'service tail stderr')
    })
    child.on('error', (err) => {
      ctx.logger.debug(
        { err, serviceName, path },
        'service tail child errored',
      )
    })

    const handle: ServiceTailHandle = {
      serviceName,
      dispose: () => {
        // Flush trailing partial line (if any) before final batcher flush so
        // a service that printed half a line at shutdown still surfaces it.
        if (stdoutPartial.length > 0) {
          batcher.addStdoutLine(stdoutPartial)
          stdoutPartial = ''
        }
        batcher.dispose()
        try {
          child.kill('SIGTERM')
        } catch (err) {
          ctx.logger.debug(
            { err, serviceName },
            'service tail SIGTERM failed (best-effort)',
          )
        }
      },
    }
    return handle
  }
}

interface HealthcheckResult {
  /** True if a probe returned exit 0 before the deadline. */
  ok: boolean
  /** Exit code of the final probe attempt; -1 if the executor itself threw. */
  lastExitCode: number
  /** Captured stdout of the final probe attempt, capped to PROBE_TAIL_MAX_BYTES. */
  lastStdoutTail: string
  /** Captured stderr of the final probe attempt, capped to PROBE_TAIL_MAX_BYTES. */
  lastStderrTail: string
}

interface ServiceTailHandle {
  readonly serviceName: string
  /** Idempotent. Flushes the trailing batcher chunk + SIGTERMs the `tail` child. */
  dispose: () => void
}

/**
 * Cap a captured probe stdout/stderr blob at a hard byte budget. We keep the
 * TAIL (last bytes) because failure context typically lives at the end —
 * stack-frame top, "connection refused" line, last health-probe response.
 */
function capTailBytes(input: string, maxBytes: number): string {
  if (typeof input !== 'string') return ''
  // Cheap fast-path — JavaScript strings are UTF-16, so .length × 2 is an
  // upper bound on UTF-8 byte size. If even that is under budget we skip the
  // expensive Buffer probe.
  if (input.length <= maxBytes / 2) return input
  const buf = Buffer.from(input, 'utf8')
  if (buf.byteLength <= maxBytes) return input
  // Slice tail. Decode again to a string so the wire payload is text.
  return buf.subarray(buf.byteLength - maxBytes).toString('utf8')
}

function sleepWithAbort(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal.aborted) {
      resolve()
      return
    }
    const onAbort = () => {
      clearTimeout(timer)
      resolve()
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}
