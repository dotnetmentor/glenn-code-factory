// HooksModule — Card 6 of the daemon-hooks-runner spec.
//
// Orchestrates HookExecutor runs across the four hook points (beforePrompt,
// afterPrompt, onFileChange, beforeCommit). Pure dispatch + sequential
// execution + result aggregation. SignalR fan-out, file watching, and
// self-heal coordination are deliberately NOT in scope here — they layer on
// top in cards 7/8/9. The module is a one-shot dispatcher: events flow out
// through `ctx.onEvent` (no EventEmitter on the class itself) so consumers
// don't need to subscribe long-term.
//
// Design notes worth keeping near the code:
//
//   - Hot-swap config: setConfig stashes into `#config`. Each `run()` snapshots
//     the relevant array AT START. An in-flight run never picks up a mid-flight
//     setConfig change — that race is closed by snapshotting the array
//     reference into a local before iterating.
//
//   - Kill switch: short-circuits to `ranAll: true` (success-shaped) so callers
//     don't treat suppression as failure. We log once (info) and emit no
//     started/completed events.
//
//   - Progress events are post-hoc: HookExecutor returns `onProgressLines` in
//     the final HookResult, not as a stream. We synthesise progress events
//     between started and completed by replaying that array in order. The
//     audit trail and UI tape both tolerate the slight skew; we don't try to
//     bridge a streaming model here.
//
//   - Silent mode is silent — even on non-zero exit, a silent hook does NOT
//     stop the loop and does NOT contribute to `failures`. `failures` drives
//     self-heal (Card 9), and a silent hook explicitly opts out of that.
//
//   - Config errors short-circuit the loop (emit `configError` event instead
//     of `completed`, stop). They don't add to `failures` or `feedbackTexts`
//     because they aren't the agent's fault — they're a misconfigured spec.
//
//   - Aborted-during-run shows up as `exitCode: null, timedOut: false` from
//     the executor. We treat that as a normal failure for `on-failure` mode
//     (push to failures, build feedback text, stop). The aborted-before-run
//     path returns immediately with a no-op result.

import { randomUUID } from 'node:crypto'
import type { Logger } from 'pino'

import { HookExecutor, type HookSpec, type HookResult } from './HookExecutor.js'

export type HookPoint = 'beforePrompt' | 'afterPrompt' | 'onFileChange' | 'beforeCommit'

export interface HookConfig {
  beforePrompt: HookSpec[]
  afterPrompt: HookSpec[]
  onFileChange: (HookSpec & { pattern: string })[]
  beforeCommit: HookSpec[]
}

export const HOOK_CONFIG_EMPTY: HookConfig = {
  beforePrompt: [],
  afterPrompt: [],
  onFileChange: [],
  beforeCommit: [],
}

export type HookLifecycleEvent =
  | { type: 'started'; spec: HookSpec; point: HookPoint; executionId: string; startedAt: Date }
  | { type: 'progress'; executionId: string; line: string; lineIndex: number }
  | {
      type: 'completed'
      spec: HookSpec
      point: HookPoint
      executionId: string
      result: HookResult
      endedAt: Date
    }
  | {
      type: 'configError'
      spec: HookSpec
      point: HookPoint
      executionId: string
      reason: string
      outputTail: string
      endedAt: Date
    }

export interface HookRunCtx {
  point: HookPoint
  conversationId?: string
  turnId?: string
  signal: AbortSignal
  onEvent: (e: HookLifecycleEvent) => void
}

export interface HookRunResult {
  ranAll: boolean
  failures: { spec: HookSpec; result: HookResult }[]
  feedbackTexts: string[]
}

export interface HooksModuleOptions {
  executor: HookExecutor
  logger: Logger
  /** Test seam. Defaults to randomUUID(). */
  generateExecutionId?: () => string
}

export class HooksModule {
  readonly #executor: HookExecutor
  readonly #logger: Logger
  readonly #generateExecutionId: () => string

  #config: HookConfig = HOOK_CONFIG_EMPTY
  #killSwitchEnabled = false

  constructor(opts: HooksModuleOptions) {
    this.#executor = opts.executor
    this.#logger = opts.logger.child({ module: 'hooks' })
    this.#generateExecutionId = opts.generateExecutionId ?? (() => randomUUID())
  }

  /**
   * Replace the active config. Hot-swappable: in-flight `run()` calls have
   * already snapshotted their spec array and are unaffected; the next `run()`
   * picks up the new config.
   */
  setConfig(cfg: HookConfig): void {
    this.#config = cfg
  }

  /**
   * Enable/disable the global hook kill switch. When enabled, `run()` returns
   * immediately with a success-shaped result and does NOT call the executor.
   */
  setKillSwitch(disabled: boolean): void {
    this.#killSwitchEnabled = disabled
  }

  async run(point: HookPoint, ctx: HookRunCtx): Promise<HookRunResult> {
    if (this.#killSwitchEnabled) {
      this.#logger.info({ point }, 'hooks suppressed by kill switch')
      return { ranAll: true, failures: [], feedbackTexts: [] }
    }

    // Already-aborted short-circuit. Don't spawn anything, don't emit events,
    // and surface ranAll=false so callers know the run was cut short.
    if (ctx.signal.aborted) {
      return { ranAll: false, failures: [], feedbackTexts: [] }
    }

    // Snapshot specs at start. A mid-flight setConfig() must NOT mutate the
    // iteration order or membership of this run.
    const specs = this.#config[point]

    this.#logger.info({ point, count: specs.length }, 'hook run started')

    const failures: { spec: HookSpec; result: HookResult }[] = []
    const feedbackTexts: string[] = []
    let ranAll = true

    for (const spec of specs) {
      const executionId = this.#generateExecutionId()
      const startedAt = new Date()

      ctx.onEvent({ type: 'started', spec, point, executionId, startedAt })

      const result = await this.#executor.run(spec, ctx.signal)
      const endedAt = new Date()

      // Config error: short-circuit. Spec is misconfigured, not the agent's
      // fault — no failure tracking, no feedback text, just halt and surface
      // a configError event so the UI/audit can show what broke.
      if (result.wasConfigError) {
        ctx.onEvent({
          type: 'configError',
          spec,
          point,
          executionId,
          reason: 'hook command failed configuration check',
          outputTail: result.outputTail,
          endedAt,
        })
        ranAll = false
        break
      }

      // Replay progress lines as `progress` events between started and
      // completed. Silent mode suppresses these — the started/completed pair
      // still fires for audit, but the live tape stays quiet.
      if (spec.feedbackMode !== 'silent') {
        for (let i = 0; i < result.onProgressLines.length; i++) {
          ctx.onEvent({
            type: 'progress',
            executionId,
            line: result.onProgressLines[i] ?? '',
            lineIndex: i,
          })
        }
      }

      ctx.onEvent({ type: 'completed', spec, point, executionId, result, endedAt })

      // Failure detection: non-zero exit OR timed out OR killed (exitCode
      // null without timedOut — usually means aborted mid-run).
      const failed = result.exitCode !== 0 || result.timedOut

      if (failed) {
        this.#logger.warn(
          { hook: spec.name, exitCode: result.exitCode },
          'hook failed',
        )
      }

      switch (spec.feedbackMode) {
        case 'on-failure':
          if (failed) {
            failures.push({ spec, result })
            // Self-heal coordinator (Card 9) wraps this in a templated prompt;
            // the module just supplies the raw tail.
            feedbackTexts.push(result.outputTail)
            ranAll = false
            // Stop the loop on first failure in on-failure mode. The agent
            // gets one chance to self-heal before later hooks run.
            return { ranAll, failures, feedbackTexts }
          }
          break

        case 'always':
          // Always-mode never stops the loop and always produces feedback,
          // so the agent learns from both successes and failures.
          if (failed) {
            feedbackTexts.push(
              `Hook \`${spec.name}\` failed (exit ${result.exitCode}).\n\n${result.outputTail}`,
            )
          } else {
            feedbackTexts.push(
              `Hook \`${spec.name}\` succeeded.\n\n${result.outputTail}`,
            )
          }
          break

        case 'silent':
          // Silent: no progress events, no feedback text, no failure tracking.
          // Even on failure, do not stop the loop — silent opts out of self-heal.
          break
      }
    }

    this.#logger.info(
      { point, ranAll, failures: failures.length },
      'hook run finished',
    )

    return { ranAll, failures, feedbackTexts }
  }
}
