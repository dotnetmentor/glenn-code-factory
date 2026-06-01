// FetchingStage — fetches the full BootstrapPayloadV2 from main API exactly
// once per daemon boot and stashes it in `BootstrapState` so downstream stages
// (WritingConfig, CloningRepo, RunningSetup, StartingServices) can read from a
// single shared source of truth.
//
// Replaces the per-resource fetch stages (BootstrapEnvStage, BootstrapMcpStage)
// when the daemon is run with the full state machine. Those stages still ship
// for the legacy short-form bootstrap path; FetchingStage is what the new
// state-machine path runs.
//
// === Errors ===
//
//   - Recoverable: `signalr.invoke('GetBootstrap')` rejects (network blip, hub
//     reconnect window, transient method-not-found during deploy). Orchestrator
//     retries with [1s,2s,4s,8s,30s] backoff.
//
//   - Fatal: payload version doesn't match a version we know how to consume.
//     `bootstrap_version_mismatch` — retrying won't help; the fix is on the
//     server or the daemon (rotate one or the other).
//
//   - Fatal: malformed payload (missing top-level fields, wrong shapes). Same
//     reasoning — the server is broken; retry won't fix it.

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'
import type { BootstrapState } from '../BootstrapState.js'

/** Versions this daemon knows how to consume. */
const SUPPORTED_VERSIONS = ['v2'] as const

const DEFAULT_TIMEOUT_MS = 15_000

export interface FetchingStageDeps {
  signalr: Pick<SignalRClient, 'getBootstrap' | 'reportBootstrapProgress'>
  state: BootstrapState
  /** Override timeout for tests (default 15s). */
  timeoutMs?: number
}

export class FetchingStage implements BootstrapStage {
  readonly name = 'fetching'

  readonly #signalr: Pick<SignalRClient, 'getBootstrap' | 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #timeoutMs: number

  constructor(deps: FetchingStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#timeoutMs = deps.timeoutMs ?? DEFAULT_TIMEOUT_MS
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    void this.#emit(ctx, 'started')

    let payload: BootstrapPayloadV2
    try {
      payload = await raceTimeout(
        this.#signalr.getBootstrap(),
        this.#timeoutMs,
        ctx.signal,
      )
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      return { ok: false, reason: `getBootstrap failed: ${reason}`, recoverable: true }
    }

    if (typeof payload?.version !== 'string') {
      void this.#emit(ctx, 'failed', 'malformed payload')
      return {
        ok: false,
        reason: 'bootstrap payload missing version field',
        recoverable: false,
      }
    }
    if (!SUPPORTED_VERSIONS.includes(payload.version as (typeof SUPPORTED_VERSIONS)[number])) {
      void this.#emit(ctx, 'failed', `version=${payload.version}`)
      return {
        ok: false,
        reason: `bootstrap_version_mismatch: server=${payload.version}, supported=${SUPPORTED_VERSIONS.join(',')}`,
        recoverable: false,
      }
    }
    if (
      typeof payload.runtimeSpec !== 'object' ||
      payload.runtimeSpec === null ||
      !Array.isArray(payload.envVars) ||
      !Array.isArray(payload.mcps)
    ) {
      void this.#emit(ctx, 'failed', 'shape')
      return {
        ok: false,
        reason: 'bootstrap payload shape invalid',
        recoverable: false,
      }
    }

    this.#state.setPayload(payload)
    ctx.logger.info(
      {
        version: payload.version,
        envVars: payload.envVars.length,
        mcps: payload.mcps.length,
        services: (payload.runtimeSpec.services ?? []).length,
        installLen: payload.runtimeSpec.install?.length ?? 0,
        setupLen: payload.runtimeSpec.setup?.length ?? 0,
        repo: payload.repo !== null,
      },
      'bootstrap payload fetched',
    )
    void this.#emit(ctx, 'completed')
    return { ok: true }
  }

  async #emit(
    ctx: BootstrapContext,
    status: 'started' | 'completed' | 'failed',
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
 * Race a promise against a timeout + the bootstrap AbortSignal. Settles with
 * either the original result, an aborted error, or a timeout error.
 */
function raceTimeout<T>(promise: Promise<T>, ms: number, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error('aborted'))
      return
    }
    const timer = setTimeout(() => {
      cleanup()
      reject(new Error(`timed out after ${ms}ms`))
    }, ms)
    const onAbort = () => {
      cleanup()
      reject(new Error('aborted'))
    }
    const cleanup = () => {
      clearTimeout(timer)
      signal.removeEventListener('abort', onAbort)
    }
    signal.addEventListener('abort', onAbort, { once: true })
    promise.then(
      (v) => {
        cleanup()
        resolve(v)
      },
      (err) => {
        cleanup()
        reject(err instanceof Error ? err : new Error(String(err)))
      },
    )
  })
}
