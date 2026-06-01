// BootstrapState — shared mutable carrier for data threaded between bootstrap
// stages.
//
// `BootstrapContext` (the per-stage argument the orchestrator hands out) is
// intentionally immutable + per-stage; it carries config / signalr / logger /
// signal but no shared state. The bootstrap state machine, however, fetches a
// `BootstrapPayloadV2` once in `FetchingStage` and every subsequent stage
// (writing config, cloning repo, running setup, starting services) reads from
// it. That cross-stage data lives here.
//
// We keep this as a tiny class (not a plain object) for two reasons:
//
//   1. The `payload` getter throws when read before `FetchingStage` writes —
//      cheaper than every stage repeating the `if (state.payload === null)`
//      defensive check.
//   2. Future stages (e.g. a hypothetical `CleanupStage`) might want to stash
//      additional cross-stage state without ballooning the constructor of
//      every other stage. Adding a field here is one place.

import type { BootstrapPayloadV2 } from '../signalr/types.js'

export class BootstrapState {
  #payload: BootstrapPayloadV2 | null = null

  /**
   * Reads the fetched bootstrap payload. Throws when called before
   * `FetchingStage` has populated the carrier — that's a programming error
   * (stages running out of order); a better outcome than every reader
   * repeating a defensive null check that never fires in practice.
   */
  get payload(): BootstrapPayloadV2 {
    if (this.#payload === null) {
      throw new Error('BootstrapState.payload read before FetchingStage populated it')
    }
    return this.#payload
  }

  /**
   * True after `FetchingStage` has stashed the payload. Used by stages that
   * want to short-circuit when the fetch failed (defence-in-depth — the
   * orchestrator already aborts on a failed stage).
   */
  hasPayload(): boolean {
    return this.#payload !== null
  }

  /**
   * Stash the fetched payload. Called exactly once from `FetchingStage`.
   * Subsequent calls overwrite — useful for tests; production stages don't
   * call this twice.
   */
  setPayload(payload: BootstrapPayloadV2): void {
    this.#payload = payload
  }
}
