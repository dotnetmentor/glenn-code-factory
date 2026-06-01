import { access } from 'node:fs/promises'

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'

/**
 * Confirms `/data/project` is mounted before later stages try to use it.
 *
 * Recoverable: the volume mount is performed by main API's
 * RuntimeProvisionerJob *after* the Machine starts, so there is a small race
 * where the daemon boots before the mount is visible. We retry with backoff
 * until it appears (or the orchestrator gives up after MAX_ATTEMPTS).
 */
export class VerifyEnvStage implements BootstrapStage {
  readonly name = 'verify-env'
  readonly #path: string

  constructor(opts: { path?: string } = {}) {
    this.#path = opts.path ?? '/data/project'
  }

  async run(_ctx: BootstrapContext): Promise<BootstrapStageResult> {
    try {
      await access(this.#path)
      return { ok: true }
    } catch {
      return {
        ok: false,
        reason: `${this.#path} not mounted yet`,
        recoverable: true,
      }
    }
  }
}
