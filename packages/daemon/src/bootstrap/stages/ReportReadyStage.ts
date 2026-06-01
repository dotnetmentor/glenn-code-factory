import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'

/**
 * Final stage — tells main API the runtime is ready to serve turns.
 *
 * Recoverable: a hub-side hiccup (reconnect window, transient method-not-found
 * during deploy) should be retried, not aborted.
 *
 * Uses the typed `signalr.runtimeReady()` wrapper which today rides on the
 * EmitEvent carrier (sessionId='', AssistantText) under the hood — once a
 * dedicated `RuntimeReady` hub method ships on the .NET side, the wrapper
 * switches over without touching this stage.
 */
export class ReportReadyStage implements BootstrapStage {
  readonly name = 'report-ready'

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    try {
      await ctx.signalr.runtimeReady()
      return { ok: true }
    } catch (err) {
      return {
        ok: false,
        reason: err instanceof Error ? err.message : String(err),
        recoverable: true,
      }
    }
  }
}
