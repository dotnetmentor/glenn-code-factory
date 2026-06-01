// RuntimeSpecApplier — runtime "live mutation" path of runtime curation.
//
// Receives an `ApplyRuntimeSpecDelta` push from the backend, walks the V2
// delta (`RuntimeSpecDeltaV2`) and applies it incrementally: runs install
// bash for any scope whose hash changed, writes supervisord conf for
// new/changed services, runs the new setup bash, and acks back via
// `RuntimeSpecDeltaApplied`.
//
// === V2 delta shape ===
//
// The delta payload is `{ proposalId, delta: RuntimeSpecDeltaV2 }`. The V2
// delta has four buckets:
//   - `newOrChangedServices: ServiceSpec[]` — new or shape-changed services.
//     Each entry may carry its own `install` bash. For each entry we first
//     compare its install-hash against the cached one (see InstallHashStore);
//     if it differs we run that install snippet BEFORE handing the spec to
//     supervisord so supervisord doesn't try to exec a binary that hasn't
//     been installed yet (which would crash-loop). Then we pass the full
//     `ServiceSpec` to `supervisord.addService`; the controller is idempotent
//     so unchanged confs are no-ops, and changed confs trigger a
//     `reread + update`. For shape-changed services (same name, different
//     command) we additionally issue `supervisorctl restart <name>` so
//     supervisord actually re-execs with the updated conf — `update` alone
//     does NOT restart a still-running program if its conf changed.
//   - `removedServices: string[]` — Phase-2 policy is **reconciling**: for
//     each removed name we tear down the supervisord program (stop +
//     remove), delete its conf file from `<confDir>/<name>.conf`, and purge
//     its hash from the install cache. The service's data directory under
//     `/data/project/services/<name>/` is **preserved** so a re-add round-
//     trips back to a working service against the same data (postgres data
//     survives a remove-then-readd). The "are you sure you want to remove"
//     gate lives one level up: the user must approve the proposal before
//     it reaches the daemon, so by the time we see the delta the removal
//     is already user-intentional. Without this teardown, stale
//     `.conf` files keep supervisord crash-looping after a service is
//     dropped from the spec (the original Phase-1 bug).
//   - `installChanged` / `installNew` — the top-level install bash. We hash
//     `installNew` against the cached top-level hash; if they differ we run
//     it via `bash -c` (same shape as InstallStage) and on success persist
//     the new hash. Hash-unchanged is a no-op even if the delta flags
//     `installChanged: true` — we trust the hash, not the flag.
//   - `setupChanged` / `setupNew` — re-run the new bash via `bash -c` from
//     the repo dir. Mirrors what RunningSetupStage does on boot.
//
// === Apply ordering ===
//
// 1. Top-level install (if hash changed) — runs first because per-service
//    installs may depend on top-level toolchains being on PATH.
// 2. For each `newOrChangedServices` entry, in order:
//    a. Run that service's per-service install if its hash changed.
//    b. `supervisord.addService(spec)` + `supervisorctl restart`.
//    The order matters: addService writes the supervisord conf which can
//    immediately start the program. If the install hasn't run yet,
//    supervisord exec's a missing binary and crash-loops.
// 3. Removed services (warn-only, leave running).
// 4. Setup re-run (if changed).
//
// === Hash persistence on per-scope success ===
//
// We persist each successful scope's hash immediately rather than batching
// at the end. A partial failure (top-level OK, service install fails)
// should leave the top-level hash recorded so the next apply doesn't re-run
// the part that already succeeded. The store's write is a small atomic
// rename so the cost is negligible.
//
// === Concurrency: serialised via #chain ===
//
// Same trick GitModule + EnvVarManager use: every call tail-chains onto a
// private `#chain` Promise so two concurrent applies don't race on
// supervisord. A failed apply does NOT poison follow-ups — the rejection is
// caught and replaced with `undefined` on the chain itself; only the caller's
// promise sees the failure.
//
// Because we ack failures via SignalR (rather than rethrowing), the daemon
// always stays up. The error becomes a `RuntimeSpecDeltaApplyResult` with
// `success: false` plus the first error message; the proposal flow on the
// backend flips the row to `Failed` and surfaces it to the user.

import type { Logger } from 'pino'

import type { IExecutor } from './IExecutor.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  ApplyRuntimeSpecDeltaPayload,
  RuntimeSpecDeltaApplyResultPayload,
} from '../signalr/types.js'
import {
  RuntimeEventTypes,
  type RuntimeEventEmitter,
} from '../events/RuntimeEventEmitter.js'
import { OutputTailBuffer } from '../logs/OutputTailBuffer.js'

import {
  InstallHashStore,
  sha256Hex,
  type InstallHashes,
} from './InstallHashStore.js'
import type {
  ServiceSpec,
  SupervisordController,
} from './SupervisordController.js'
import { BOOTSTRAP_DEFAULT_PATH } from './BootstrapEnvironment.js'

const DEFAULT_REPO_DIR = '/data/project/repo'
const DEFAULT_INSTALL_CWD = '/'
// Centralized so dry-run + install + setup never drift.
const DEFAULT_PATH = BOOTSTRAP_DEFAULT_PATH
const DEFAULT_SETUP_TIMEOUT_MS = 10 * 60_000
const DEFAULT_INSTALL_TIMEOUT_MS = 10 * 60_000

export interface RuntimeSpecApplierDeps {
  signalr: SignalRClient
  supervisord: SupervisordController
  executor: IExecutor
  logger: Logger
  /**
   * Persistent per-scope install-hash cache. Optional — defaults to a fresh
   * `InstallHashStore` against the production path. Tests inject a stub
   * pointing at an in-memory fs.
   */
  hashStore?: InstallHashStore
  /** Override repo dir for tests (default `/data/project/repo`). */
  repoDir?: string
  /** Override cwd for the install bash (default `/`). */
  installCwd?: string
  /** Override the PATH for the setup / install bash. */
  path?: string
  /** Override the timeout for the setup bash (default 10 min). */
  setupTimeoutMs?: number
  /** Override the timeout for install bash (default 10 min). */
  installTimeoutMs?: number
  /**
   * Structured RuntimeEvent emitter. When provided, the applier emits a
   * `SpecDeltaApplied` (Info) on success or `SpecDeltaFailed` (Error) on
   * failure, with mandatory `durationMs` and a `phaseTimings` breakdown of
   * the install / services / setup phases. Optional for back-compat with
   * older tests that wire the applier without the emitter.
   */
  emitter?: RuntimeEventEmitter
  /** Monotonic clock for phase timings — defaults to `Date.now`. */
  now?: () => number
}

export class RuntimeSpecApplier {
  readonly #signalr: SignalRClient
  readonly #supervisord: SupervisordController
  readonly #executor: IExecutor
  readonly #logger: Logger
  readonly #hashStore: InstallHashStore
  readonly #repoDir: string
  readonly #installCwd: string
  readonly #path: string
  readonly #setupTimeoutMs: number
  readonly #installTimeoutMs: number
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #now: () => number
  // Sequential dispatch — same shape as GitModule.#chain and EnvVarManager.#chain.
  // A failed task does NOT poison follow-ups; the failure is re-surfaced on
  // the caller's promise but the chain itself is reset to a resolved promise.
  #chain: Promise<unknown> = Promise.resolve()

  constructor(deps: RuntimeSpecApplierDeps) {
    this.#signalr = deps.signalr
    this.#supervisord = deps.supervisord
    this.#executor = deps.executor
    this.#logger = deps.logger.child({ module: 'runtime-spec-applier' })
    this.#hashStore = deps.hashStore ?? new InstallHashStore()
    this.#repoDir = deps.repoDir ?? DEFAULT_REPO_DIR
    this.#installCwd = deps.installCwd ?? DEFAULT_INSTALL_CWD
    this.#path = deps.path ?? DEFAULT_PATH
    this.#setupTimeoutMs = deps.setupTimeoutMs ?? DEFAULT_SETUP_TIMEOUT_MS
    this.#installTimeoutMs = deps.installTimeoutMs ?? DEFAULT_INSTALL_TIMEOUT_MS
    this.#emitter = deps.emitter
    this.#now = deps.now ?? Date.now
  }

  /**
   * Apply a runtime-spec delta. Serialised via {@link #chain} — concurrent
   * calls run sequentially. Resolves once the ack has been sent (success or
   * failure); never rejects.
   */
  async applyDelta(
    payload: ApplyRuntimeSpecDeltaPayload,
    signal?: AbortSignal,
  ): Promise<void> {
    const next = this.#chain
      .catch(() => undefined)
      .then(() => this.#runDelta(payload, signal))
    // Swallow the rejection on the chain itself so a failed op doesn't poison
    // every subsequent op. Caller still sees the rejection via `next` — but
    // `#runDelta` itself catches everything and never rejects, so this is
    // belt-and-braces.
    this.#chain = next.catch(() => undefined)
    return next
  }

  async #runDelta(
    payload: ApplyRuntimeSpecDeltaPayload,
    signal?: AbortSignal,
  ): Promise<void> {
    const delta = payload.delta
    const newOrChangedServices = delta.newOrChangedServices ?? []
    const removedServices = delta.removedServices ?? []

    const deltaSummary = {
      servicesAdded: newOrChangedServices.length,
      servicesRemoved: removedServices.length,
      installChanged: delta.installChanged === true,
      setupChanged: delta.setupChanged === true,
    }

    this.#logger.info(
      {
        proposalId: payload.proposalId,
        ...deltaSummary,
      },
      'apply_delta_start',
    )

    // Paired SpecDeltaApplied / SpecDeltaFailed events. We use startTimer so
    // the success/failure events auto-carry durationMs; phaseTimings is
    // tracked separately below and added to the success payload.
    const timer = this.#emitter?.startTimer(
      RuntimeEventTypes.SpecDeltaApplied,
      { proposalId: payload.proposalId, deltaSummary },
    )

    // Per-phase timings. Each phase that runs records elapsed ms; phases that
    // are skipped (hash unchanged, flag false) report 0 so the consumer can
    // see at a glance which phases actually did work.
    const phaseTimings = { installMs: 0, servicesMs: 0, setupMs: 0 }

    let firstError: string | undefined

    try {
      // Phase 1: install. Hash-gated — we trust the hash, not the
      // `installChanged` flag. Even if the caller flips the flag, an
      // unchanged hash means "same script, no work to do".
      //
      // We snapshot the cached hashes ONCE here so we can compare both
      // top-level and per-service without a re-read mid-loop. Mutations
      // happen on this in-memory copy; we write the whole snapshot back to
      // disk after each successful scope so a partial-failure run still
      // records what succeeded.
      let cached: InstallHashes
      try {
        cached = await this.#hashStore.read()
      } catch (err) {
        // The store treats ENOENT / malformed JSON as empty; anything else
        // is unexpected. Same safe default as InstallStage: treat as empty
        // and re-run install. Skipping when we shouldn't is the only bad
        // outcome, so erring on the side of re-running is correct.
        this.#logger.warn({ err }, 'install hash cache read failed; treating as empty')
        cached = { topLevel: '', services: {} }
      }

      if (delta.installChanged) {
        if (signal?.aborted === true) {
          throw new Error('apply_delta aborted at top-level install')
        }
        const installPhaseStart = this.#now()
        const installNew = (delta.installNew ?? '').trim()
        const desiredTopLevelHash = sha256Hex(delta.installNew ?? '')
        // Paired InstallStarted/InstallCompleted|Skipped|Failed for the
        // top-level scope. No `service` field — that's the convention the
        // backend timeline uses to know "this was the top-level scope".
        const topInstallTimer = this.#emitter?.startTimer(
          RuntimeEventTypes.InstallStarted,
          {
            hash: desiredTopLevelHash,
            hashShort: desiredTopLevelHash.slice(0, 12),
            scope: 'top-level',
            proposalId: payload.proposalId,
            installBytes: installNew.length,
          },
        )
        if (desiredTopLevelHash === cached.topLevel) {
          this.#logger.info(
            { proposalId: payload.proposalId },
            'apply_delta: top-level install hash unchanged — skipping',
          )
          topInstallTimer?.skip(RuntimeEventTypes.InstallSkipped, {
            hash: desiredTopLevelHash,
            hashShort: desiredTopLevelHash.slice(0, 12),
            scope: 'top-level',
            reason: 'hash unchanged',
          })
        } else if (installNew.length === 0) {
          // Spec cleared the top-level install. Hash differs (was non-empty,
          // now empty). Nothing to run, but record the new (empty) hash so we
          // don't keep re-checking on every delta.
          this.#logger.info(
            { proposalId: payload.proposalId },
            'apply_delta: top-level install cleared — updating hash, nothing to run',
          )
          cached = { ...cached, topLevel: desiredTopLevelHash }
          await this.#persistHashes(cached, payload.proposalId)
          topInstallTimer?.skip(RuntimeEventTypes.InstallSkipped, {
            hash: desiredTopLevelHash,
            hashShort: desiredTopLevelHash.slice(0, 12),
            scope: 'top-level',
            reason: 'install cleared',
          })
        } else {
          this.#logger.info(
            {
              proposalId: payload.proposalId,
              installBytes: installNew.length,
            },
            'apply_delta: running top-level install',
          )
          try {
            await this.#runInstall(installNew, signal)
          } catch (err) {
            const reason = err instanceof Error ? err.message : String(err)
            firstError ??= reason
            topInstallTimer?.fail(RuntimeEventTypes.InstallFailed, {
              hash: desiredTopLevelHash,
              hashShort: desiredTopLevelHash.slice(0, 12),
              scope: 'top-level',
              errorMessage: reason,
            })
            throw err
          }
          cached = { ...cached, topLevel: desiredTopLevelHash }
          await this.#persistHashes(cached, payload.proposalId)
          topInstallTimer?.complete(RuntimeEventTypes.InstallCompleted, {
            hash: desiredTopLevelHash,
            hashShort: desiredTopLevelHash.slice(0, 12),
            scope: 'top-level',
          })
        }
        phaseTimings.installMs = Math.max(0, this.#now() - installPhaseStart)
      }

      // Phase 2: services. For each new/changed service:
      //   1. If its per-service install hash changed, run that install FIRST.
      //      Otherwise supervisord may try to start a not-yet-installed
      //      binary and crash-loop.
      //   2. Register the conf (the controller renders + reread+update).
      //   3. Restart so a same-name spec with a new command/user/env
      //      actually re-execs.
      const servicesPhaseStart = this.#now()
      for (const spec of newOrChangedServices) {
        if (signal?.aborted === true) {
          throw new Error(`apply_delta aborted at service ${spec.name}`)
        }
        try {
          // 2a — per-service install. Trust the hash, not the presence of
          // an `install` field: a service whose install snippet is the same
          // as last time has nothing to (re-)do.
          //
          // Each per-service install gets its own paired Started/Completed
          // (or Skipped / Failed) timer carrying `{ service: spec.name }`
          // so the timeline can render a per-service install row distinct
          // from the top-level scope's row.
          const desiredSvcHash = sha256Hex(spec.install ?? '')
          const cachedSvcHash = cached.services[spec.name] ?? ''
          const svcInstall = (spec.install ?? '').trim()
          // Skip the timer entirely for the no-op-no-op case (hash matches
          // AND there's nothing to run): emitting Started/Skipped for every
          // unchanged service on every delta would just be timeline noise.
          // We still emit when the hash differs even with an empty script,
          // because "install cleared" is a meaningful state transition.
          const shouldEmit = desiredSvcHash !== cachedSvcHash
          const svcInstallTimer = shouldEmit
            ? this.#emitter?.startTimer(RuntimeEventTypes.InstallStarted, {
                service: spec.name,
                hash: desiredSvcHash,
                hashShort: desiredSvcHash.slice(0, 12),
                scope: 'service',
                proposalId: payload.proposalId,
                installBytes: svcInstall.length,
              })
            : undefined
          if (desiredSvcHash !== cachedSvcHash) {
            if (svcInstall.length > 0) {
              this.#logger.info(
                {
                  proposalId: payload.proposalId,
                  service: spec.name,
                  installBytes: svcInstall.length,
                },
                'apply_delta: running per-service install',
              )
              try {
                await this.#runInstall(svcInstall, signal)
              } catch (err) {
                const reason = err instanceof Error ? err.message : String(err)
                svcInstallTimer?.fail(RuntimeEventTypes.InstallFailed, {
                  service: spec.name,
                  hash: desiredSvcHash,
                  hashShort: desiredSvcHash.slice(0, 12),
                  scope: 'service',
                  errorMessage: reason,
                })
                firstError ??= reason
                throw err
              }
              svcInstallTimer?.complete(RuntimeEventTypes.InstallCompleted, {
                service: spec.name,
                hash: desiredSvcHash,
                hashShort: desiredSvcHash.slice(0, 12),
                scope: 'service',
              })
            } else {
              this.#logger.info(
                { proposalId: payload.proposalId, service: spec.name },
                'apply_delta: per-service install cleared — updating hash, nothing to run',
              )
              svcInstallTimer?.skip(RuntimeEventTypes.InstallSkipped, {
                service: spec.name,
                hash: desiredSvcHash,
                hashShort: desiredSvcHash.slice(0, 12),
                scope: 'service',
                reason: 'install cleared',
              })
            }
            cached = {
              ...cached,
              services: { ...cached.services, [spec.name]: desiredSvcHash },
            }
            await this.#persistHashes(cached, payload.proposalId)
          }

          // 2b — supervisord conf. Generated `env` is
          // `Partial<Record<string, string>>`; cast at boundary since
          // runtime values are non-undefined (backend validation forbids
          // null values).
          await this.#supervisord.addService(spec as ServiceSpec, signal)

          // 2c — restart so a same-name spec with a new command/user/env
          // actually re-execs. `addService` writes the conf + reread/update,
          // but supervisord's update is a no-op for "program already running
          // with same name" — it only starts NEW programs. A subsequent
          // restart is cheap (no-op when the conf was unchanged:
          // supervisorctl restart bounces the program but the next
          // iteration's healthcheck path — not exercised here, that's
          // StartingServicesStage — would catch it). We accept the bounce
          // because incoming deltas are rare.
          try {
            await this.#executor.run('supervisorctl', ['restart', spec.name], {
              allowNonZero: true,
            })
          } catch (restartErr) {
            // Best-effort: the program might not exist yet on the first
            // addService and `restart` would fail with "no such process".
            // We log + continue — the addService path already started it.
            this.#logger.debug(
              { err: restartErr, service: spec.name },
              'supervisorctl restart returned non-zero (likely new-service first-add); continuing',
            )
          }
        } catch (err) {
          firstError ??= err instanceof Error ? err.message : String(err)
          throw err
        }
      }
      phaseTimings.servicesMs = Math.max(0, this.#now() - servicesPhaseStart)

      // Phase 3: removed services. Reconciling policy — tear down the
      // supervisord program (stop + remove + unlink conf) so a spec that
      // drops a service doesn't leave its `.conf` on disk causing
      // supervisord to crash-loop on the next reread/update. Data dir
      // under /data/project/services/<name>/ is preserved so re-adding the
      // service round-trips against the same data.
      //
      // Best-effort per service: a failure on one removal logs but doesn't
      // abort the whole delta. We've already applied the additive changes
      // above; recording one of those as failed because we couldn't unlink
      // a stale conf would be misleading. The hash purge that follows is
      // strict though — it's a local in-memory mutation that can't fail.
      for (const name of removedServices) {
        if (signal?.aborted === true) {
          throw new Error(`apply_delta aborted at service removal ${name}`)
        }
        try {
          const wasPresent = await this.#supervisord.removeService(name)
          this.#logger.info(
            { proposalId: payload.proposalId, service: name, wasPresent },
            'apply_delta: service removed from supervisord',
          )
        } catch (err) {
          this.#logger.warn(
            { proposalId: payload.proposalId, service: name, err },
            'apply_delta: service removal failed (continuing)',
          )
        }
        // Purge the cached install hash so a future re-add forces a fresh
        // install (rather than skipping on a stale hash match against a
        // service whose data we just left on disk). The data dir survives,
        // but the install snippet may have evolved between remove and
        // re-add, so re-running it on re-add is the correct default.
        if (cached.services[name] !== undefined) {
          const { [name]: _removed, ...rest } = cached.services
          void _removed
          cached = { ...cached, services: rest }
          await this.#persistHashes(cached, payload.proposalId)
        }
      }

      // Phase 4: setup. Re-run the new bash if it changed. Mirrors the
      // bootstrap RunningSetupStage so a user-edited setup script lands
      // mid-life without a full reboot. Same paired SetupCommandStarted /
      // SetupCommandCompleted|Failed events with a bounded stdout+stderr
      // tail on the end payload.
      if (delta.setupChanged && delta.setupNew !== undefined && delta.setupNew !== null) {
        const setupPhaseStart = this.#now()
        const setup = delta.setupNew.trim()
        if (setup.length > 0) {
          if (signal?.aborted === true) {
            throw new Error('apply_delta aborted at setup')
          }
          const setupTimer = this.#emitter?.startTimer(
            RuntimeEventTypes.SetupCommandStarted,
            {
              proposalId: payload.proposalId,
              commandBytes: setup.length,
            },
          )
          const setupTail = new OutputTailBuffer()
          try {
            await this.#executor.run('bash', ['-c', setup], {
              cwd: this.#repoDir,
              env: { PATH: this.#path, HOME: process.env['HOME'] ?? '/home/agent' },
              timeoutMs: this.#setupTimeoutMs,
              onStdout: (chunk) => setupTail.pushStdout(chunk),
              onStderr: (chunk) => setupTail.pushStderr(chunk),
            })
            setupTimer?.complete(RuntimeEventTypes.SetupCommandCompleted, {
              proposalId: payload.proposalId,
              outputTailLines: setupTail.take(),
            })
          } catch (err) {
            const reason = err instanceof Error ? err.message : String(err)
            firstError ??= reason
            setupTimer?.fail(RuntimeEventTypes.SetupCommandFailed, {
              proposalId: payload.proposalId,
              errorMessage: reason,
              outputTailLines: setupTail.take(),
            })
            throw err
          }
        }
        phaseTimings.setupMs = Math.max(0, this.#now() - setupPhaseStart)
      }

      await this.#sendAck({
        proposalId: payload.proposalId,
        success: true,
      })
      timer?.complete(RuntimeEventTypes.SpecDeltaApplied, {
        proposalId: payload.proposalId,
        deltaSummary,
        phaseTimings,
      })
      this.#logger.info(
        { proposalId: payload.proposalId, phaseTimings },
        'apply_delta_done',
      )
    } catch (err) {
      const message = firstError ?? (err instanceof Error ? err.message : String(err))
      this.#logger.error(
        { proposalId: payload.proposalId, err: message, phaseTimings },
        'apply_delta_failed',
      )
      await this.#sendAck({
        proposalId: payload.proposalId,
        success: false,
        error: message,
      })
      timer?.fail(RuntimeEventTypes.SpecDeltaFailed, {
        proposalId: payload.proposalId,
        deltaSummary,
        phaseTimings,
        errorMessage: message,
      })
      // Don't rethrow — daemon stays up. The error is reported to backend
      // via the ack above.
    }
  }

  /**
   * Execute an install script via `bash -c` with the same PATH / HOME shape
   * InstallStage uses on bootstrap. Throws on non-zero exit (propagated up
   * so the apply ack carries the error message).
   */
  async #runInstall(script: string, signal?: AbortSignal): Promise<void> {
    if (signal?.aborted === true) {
      throw new Error('apply_delta aborted before install bash')
    }
    await this.#executor.run('bash', ['-c', script], {
      cwd: this.#installCwd,
      env: {
        PATH: this.#path,
        HOME: process.env['HOME'] ?? '/home/agent',
      },
      timeoutMs: this.#installTimeoutMs,
      onStdout: (chunk) => {
        const trimmed = chunk.trim()
        if (trimmed.length > 0) this.#logger.info({ stream: 'stdout' }, trimmed)
      },
      onStderr: (chunk) => {
        const trimmed = chunk.trim()
        if (trimmed.length > 0) this.#logger.info({ stream: 'stderr' }, trimmed)
      },
    })
  }

  /**
   * Persist the current per-scope hash snapshot. A failed write is logged
   * but NOT rethrown: the install script DID run successfully and the
   * system is in the right state; the only cost of a missed write is an
   * extra (idempotent) re-run on the next apply. Mirrors InstallStage's
   * stance on hash-write failures.
   */
  async #persistHashes(hashes: InstallHashes, proposalId: string): Promise<void> {
    try {
      await this.#hashStore.write(hashes)
    } catch (err) {
      this.#logger.warn(
        { err, proposalId },
        'apply_delta: install hash persist failed; install may re-run on next delta',
      )
    }
  }

  /**
   * Send the ack. We swallow ack-time failures so a momentary signalr blip
   * doesn't turn into an unhandled rejection — the backend can recover via
   * the proposal-status read path on the next poll.
   *
   * <p>On a swallow we ALSO emit a `SpecApplyAckFailed` runtime event so
   * operators can see in the Timeline that an ack was lost even when the
   * backend never received it. The event carries:
   * <ul>
   *   <li><code>proposalId</code> — correlation key against the original delta;</li>
   *   <li><code>errorMessage</code> — first line of the thrown error;</li>
   *   <li><code>ackPayloadBytes</code> — JSON-stringified size, useful for
   *       spotting payload-too-large rejections;</li>
   *   <li><code>signalrConnectionState</code> — `"connected"` / `"disconnected"`
   *       at the moment we tried, so post-hoc analysis can tell wire-blip apart
   *       from reject-of-malformed.</li>
   * </ul>
   * The emit is itself best-effort — the emitter is internally buffered, so a
   * fully-disconnected daemon will replay this event on reconnect.</p>
   */
  async #sendAck(payload: RuntimeSpecDeltaApplyResultPayload): Promise<void> {
    try {
      await this.#signalr.invoke('RuntimeSpecDeltaApplied', payload)
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : String(err)
      let ackPayloadBytes = 0
      try {
        ackPayloadBytes = Buffer.byteLength(JSON.stringify(payload), 'utf8')
      } catch {
        // JSON.stringify failing on a payload we constructed is exceptional,
        // but the diagnostic event is more useful than a zero — keep going.
      }
      const signalrConnectionState = this.#signalr.isConnected()
        ? 'connected'
        : 'disconnected'

      this.#logger.warn(
        {
          err,
          proposalId: payload.proposalId,
          ackPayloadBytes,
          signalrConnectionState,
        },
        'failed to send RuntimeSpecDeltaApplied ack',
      )

      this.#emitter?.emit(
        RuntimeEventTypes.SpecApplyAckFailed,
        'Error',
        {
          proposalId: payload.proposalId,
          errorMessage,
          ackPayloadBytes,
          signalrConnectionState,
        },
      )
    }
  }
}
