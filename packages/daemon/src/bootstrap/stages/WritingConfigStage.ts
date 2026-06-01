// WritingConfigStage — atomically writes the daemon's on-disk config files
// from the BootstrapPayloadV1 stashed in BootstrapState by FetchingStage.
//
// Files written under the configured base dir (default `/data/.glenn`):
//
//   - `env`        — handed to EnvVarManager.loadInitial so deltas later
//                     (UpdateConfig with envVarsDelta) land on the same file.
//   - `hooks.json` — JSON-serialised payload.hooks (or `{}` when null).
//   - `mcp.json`   — JSON-serialised payload.mcps array.
//
// In-memory snapshots also seeded:
//
//   - EnvVarManager (via loadInitial) — atomic disk write + in-memory map.
//   - McpRegistry (via loadInitial) — read on every turn by CursorFactory.
//
// Atomicity for hooks.json / mcp.json: write to a sibling `.tmp` path then
// `rename` into place. Rename is atomic on POSIX as long as src + dst share a
// filesystem (they do — both live under the same dir). EnvVarManager runs its
// own atomic-rename dance.
//
// Failure mode: any disk error is fatal (`recoverable: false`). If we can't
// write to `/data/.glenn` the box is broken — supervisord will respawn the
// daemon, but the underlying problem (bad mount, ENOSPC, …) needs operator
// intervention.

import type { mkdir, rename, writeFile } from 'node:fs/promises'

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'
import type { EnvVarManager } from '../../env/EnvVarManager.js'
import type { McpEntry, McpRegistry } from '../../mcp/McpRegistry.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapState } from '../BootstrapState.js'

/**
 * Subset of `node:fs/promises` we touch. Carved out as an interface so tests
 * can hand-roll a fake without `vi.mock`. Same pattern as
 * SupervisordControllerFs.
 */
export interface WritingConfigStageFs {
  writeFile: typeof writeFile
  rename: typeof rename
  mkdir: typeof mkdir
}

const DEFAULT_BASE_DIR = '/data/.glenn'

export interface WritingConfigStageDeps {
  signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  state: BootstrapState
  fs: WritingConfigStageFs
  envVarManager: Pick<EnvVarManager, 'loadInitial'>
  mcpRegistry: Pick<McpRegistry, 'loadInitial'>
  /** Override base dir for tests (default `/data/.glenn`). */
  baseDir?: string
}

export class WritingConfigStage implements BootstrapStage {
  readonly name = 'writing-config'

  readonly #signalr: Pick<SignalRClient, 'reportBootstrapProgress'>
  readonly #state: BootstrapState
  readonly #fs: WritingConfigStageFs
  readonly #envVarManager: Pick<EnvVarManager, 'loadInitial'>
  readonly #mcpRegistry: Pick<McpRegistry, 'loadInitial'>
  readonly #baseDir: string

  constructor(deps: WritingConfigStageDeps) {
    this.#signalr = deps.signalr
    this.#state = deps.state
    this.#fs = deps.fs
    this.#envVarManager = deps.envVarManager
    this.#mcpRegistry = deps.mcpRegistry
    this.#baseDir = deps.baseDir ?? DEFAULT_BASE_DIR
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    const payload = this.#state.payload
    void this.#emit(ctx, 'started')

    try {
      // Ensure the directory exists. recursive: true is idempotent — no-op
      // when the dir already exists, no error on EEXIST.
      await this.#fs.mkdir(this.#baseDir, { recursive: true })

      // Env vars go through EnvVarManager so subsequent UpdateConfig deltas
      // land on the same file + in-memory map.
      await this.#envVarManager.loadInitial(payload.envVars)

      // MCP servers seed the in-memory registry that CursorFactory reads on
      // every turn. We also write `mcp.json` for downstream tooling that may
      // want to inspect the same MCP set out-of-band.
      const mcpEntries = payload.mcps.map<McpEntry>((m) => ({
        name: m.name,
        // `BootstrapMcpServer` only ships `name`+`url`+`scope`; the in-memory
        // entry uses `baseUrl` and a separate `version` (defaulted here since
        // it isn't on the bootstrap wire). The registry tolerates this — no
        // turn-time code reads version other than for telemetry.
        version: '1.0.0',
        baseUrl: m.url,
      }))
      this.#mcpRegistry.loadInitial(mcpEntries)

      const hooksContents = JSON.stringify(payload.hooks ?? {}, null, 2) + '\n'
      const mcpContents = JSON.stringify(payload.mcps, null, 2) + '\n'

      await this.#writeAtomic(`${this.#baseDir}/hooks.json`, hooksContents)
      await this.#writeAtomic(`${this.#baseDir}/mcp.json`, mcpContents)
    } catch (err) {
      const reason = err instanceof Error ? err.message : String(err)
      void this.#emit(ctx, 'failed', reason)
      return {
        ok: false,
        reason: `writing-config failed: ${reason}`,
        recoverable: false,
      }
    }

    ctx.logger.info(
      { baseDir: this.#baseDir, envVars: payload.envVars.length, mcps: payload.mcps.length },
      'bootstrap config files written',
    )
    void this.#emit(ctx, 'completed')
    return { ok: true }
  }

  async #writeAtomic(path: string, contents: string): Promise<void> {
    const tmp = `${path}.tmp`
    await this.#fs.writeFile(tmp, contents, 'utf8')
    await this.#fs.rename(tmp, path)
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
