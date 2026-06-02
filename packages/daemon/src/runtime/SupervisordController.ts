// SupervisordController — renders a supervisord program block from a
// generic `ServiceSpec` object and applies it via supervisorctl.
//
// === V2 contract ===
//
// V1 of this module shipped a hardcoded `SERVICE_TEMPLATES` catalog (postgres,
// redis, minio, mailhog) and resolved a service by name. V2 (Runtime Spec V2,
// see /.sdd/specifications/runtime-spec-v2.md) removes the catalog entirely:
// the caller hands us a `ServiceSpec` object and we render whatever the spec
// declares. Adding MongoDB, Elasticsearch, ClickHouse — anything supervisord
// can supervise — no longer requires a code change here.
//
// The local `ServiceSpec` / `HealthcheckSpec` types defined below mirror the
// C# records at
//   /workspace/packages/dotnet-api/Source/Features/RuntimeBootstrap/Contracts/RuntimeSpecV2.cs
// They're marked `[TranspilationSource]` on the C# side and the Tapper-
// generated TS twins live under `src/generated/signalr/`. We re-declare them
// here (rather than importing from `generated/`) for two reasons:
//   1. The generated file for `RuntimeBootstrap.Contracts` is regenerated
//      alongside the C# records on the backend side and hasn't been refreshed
//      for V2 yet — this controller needs to compile *now* so the daemon
//      stays green during parallel work.
//   2. Owning the type locally keeps the contract narrow: this module only
//      cares about the conf-rendering subset of `ServiceSpec`, not the
//      full V2 bootstrap payload shape.
// When the generated Tapper types catch up to V2, a follow-up card can swap
// the local types for the imports — the field shape is byte-identical.
//
// === Contract this module owns ===
//
//   - Idempotent: if `<confDir>/<spec.name>.conf` already exists with byte-
//     identical content to what we'd render, this is a no-op (no writes, no
//     reread/update). If the existing file has different content (spec edit,
//     drift recovery), we overwrite + reread + update.
//   - Rendering: one supervisord program block per ServiceSpec. Fields with
//     defaults: `user` → `agent`, `autorestart` → `true`. Healthcheck is NOT
//     rendered into the conf — supervisord has no native healthcheck; the
//     daemon polls via a separate path (see StartingServicesStage / the
//     dedicated health-polling card).
//   - Failure surface: writeFile / supervisorctl errors propagate to the
//     caller (RuntimeSpecApplier converts to a failed delta-apply ack).
//
// === Permissions / sudo ===
//
// In production the daemon runs as the `agent` user, but supervisorctl + the
// conf-file writes need root. The runtime base image grants passwordless
// `sudo -n` for the agent user; the {@link IExecutor} this controller is
// wired with in production should therefore be a `sudo`-prefixing wrapper
// around `ChildProcessExecutor`. Tests inject a fake executor so the sudo
// dance is a non-issue in unit tests. See main.ts for production wiring.

import type { access, readdir, readFile, unlink, writeFile } from 'node:fs/promises'
import type { Logger } from 'pino'

import type { IExecutor } from './IExecutor.js'

/**
 * Health-check definition for a single service. Mirrors the C# record
 * `Source.Features.RuntimeBootstrap.Contracts.HealthcheckSpec`. Carried
 * through `ServiceSpec` but NOT rendered into supervisord conf — the daemon's
 * health-poller consumes this directly.
 */
export interface HealthcheckSpec {
  /** Shell command whose exit code determines health. Exit 0 = healthy. */
  command: string
  /** Poll interval in seconds. Daemon default (5s) applied when omitted. */
  intervalSeconds?: number
}

/**
 * One supervised process. Mirrors the C# record
 * `Source.Features.RuntimeBootstrap.Contracts.ServiceSpec`. Renders to a
 * single supervisord `[program:<name>]` block.
 */
export interface ServiceSpec {
  /** Unique-within-spec identifier. Becomes the supervisord program name. */
  name: string
  /** Command supervisord runs — passed verbatim, no shell interpretation. */
  command: string
  /** Unix user. `undefined` → defaults to `agent`. */
  user?: string
  /** Auto-restart on non-zero exit. `undefined` → defaults to `true`. */
  autorestart?: boolean
  /** Per-service env vars merged on top of the inherited environment. */
  env?: Record<string, string>
  /**
   * Health-check definition. Carried for downstream consumers (the daemon
   * health-poller) — explicitly NOT rendered into the supervisord conf.
   */
  healthcheck?: HealthcheckSpec
  /** Per-service install bash. Consumed by the install stage, not by us. */
  install?: string
}

/**
 * Subset of `node:fs/promises` we touch. Carved out as an interface so tests
 * can hand-roll a fake without `vi.mock`.
 */
export interface SupervisordControllerFs {
  readFile: typeof readFile
  writeFile: typeof writeFile
  access: typeof access
  readdir: typeof readdir
  unlink: typeof unlink
}

const DEFAULT_CONF_DIR = '/etc/supervisor/conf.d'
const DEFAULT_USER = 'agent'
const LOG_DIR = '/var/log/supervisor'

export interface SupervisordControllerDeps {
  executor: IExecutor
  fs: SupervisordControllerFs
  logger: Logger
  /** Override the conf-d directory. Production uses `/etc/supervisor/conf.d`. */
  confDir?: string
}

export class SupervisordController {
  readonly #executor: IExecutor
  readonly #fs: SupervisordControllerFs
  readonly #logger: Logger
  readonly #confDir: string

  constructor(deps: SupervisordControllerDeps) {
    this.#executor = deps.executor
    this.#fs = deps.fs
    this.#logger = deps.logger.child({ module: 'supervisord-controller' })
    this.#confDir = deps.confDir ?? DEFAULT_CONF_DIR
  }

  /**
   * Idempotently register (and start, via supervisord) a service from a
   * `ServiceSpec`. Renders the spec into a `[program:<name>]` conf block at
   * `<confDir>/<spec.name>.conf`, then runs
   * `supervisorctl reread && supervisorctl update`.
   *
   * Idempotency: if the conf file already exists with byte-identical content
   * to what we'd render, the write AND the reread/update calls are skipped.
   * If the content differs (drift recovery, spec edit), we overwrite and
   * reread/update so supervisord picks up the new config.
   *
   * Throws: the underlying executor's error when supervisorctl fails (caller
   * converts to a delta-apply ack with success=false), or the fs error if
   * the write fails.
   *
   * AbortSignal: accepted but only checked once at the top. Long-running
   * supervisorctl calls are sub-second in practice so mid-call cancellation
   * isn't worth the complexity.
   */
  async addService(spec: ServiceSpec, signal?: AbortSignal): Promise<void> {
    if (signal?.aborted === true) {
      throw new Error(`supervisord addService aborted before start: ${spec.name}`)
    }

    const confPath = `${this.#confDir}/${spec.name}.conf`
    const desired = renderServiceBlock(spec)

    const existing = await this.#readIfExists(confPath)
    if (existing === desired) {
      this.#logger.info(
        { service: spec.name, confPath },
        'supervisord conf unchanged, skipping',
      )
      return
    }

    await this.#fs.writeFile(confPath, desired, 'utf8')
    this.#logger.info(
      {
        service: spec.name,
        confPath,
        action: existing === undefined ? 'created' : 'updated',
      },
      'supervisord conf written',
    )

    await this.#executor.run('supervisorctl', ['reread'])
    await this.#executor.run('supervisorctl', ['update'])
    this.#logger.info({ service: spec.name }, 'supervisord service started')
  }

  /**
   * Restart a supervisord program. Used after setup mutates `node_modules` so
   * Vite rebuilds its dep cache. Best-effort — callers treat non-zero exit as
   * non-fatal when the program was never registered.
   */
  async restartService(name: string, signal?: AbortSignal): Promise<void> {
    if (signal?.aborted === true) {
      throw new Error(`supervisord restartService aborted before start: ${name}`)
    }
    await this.#executor.run('supervisorctl', ['restart', name], {
      allowNonZero: true,
    })
    this.#logger.info({ service: name }, 'supervisord service restarted')
  }

  async #readIfExists(path: string): Promise<string | undefined> {
    try {
      await this.#fs.access(path)
    } catch {
      return undefined
    }
    try {
      return await this.#fs.readFile(path, 'utf8')
    } catch {
      // File appeared in access() but failed to read — treat as "missing" so
      // we re-render and overwrite. Safer than throwing on a transient race.
      return undefined
    }
  }

  /**
   * Tear down a single supervised service so a spec edit that removes it
   * stops leaving stale `.conf` files on disk for supervisord to crash-loop
   * against on the next reload.
   *
   * Safe to call for a name that was never registered: each step is wrapped
   * in a best-effort try/catch and tolerates "no such process" / "no such
   * file". The data directory under `/data/project/services/<name>/` is
   * deliberately NOT touched — re-adding the service later picks up where it
   * left off (e.g. postgres data survives a removal-then-readd round-trip).
   *
   * Steps, in order:
   *   1. `supervisorctl stop <name>` — kill the running process if any.
   *   2. `supervisorctl remove <name>` — drop the program from supervisord's
   *      in-memory registry so a follow-up `reread + update` doesn't try to
   *      re-spawn it from a still-cached state.
   *   3. `unlink` the conf file at `<confDir>/<name>.conf` so the next
   *      `supervisorctl reread` doesn't re-discover the program.
   *   4. `supervisorctl reread && supervisorctl update` to commit the
   *      removal to supervisord's view of the world.
   *
   * Returns true if anything was actually removed (conf existed before the
   * call), false if the service was already absent. The boolean is mostly
   * useful for caller-side logging — both outcomes are valid "service is
   * gone now" terminal states.
   */
  async removeService(name: string): Promise<boolean> {
    const confPath = `${this.#confDir}/${name}.conf`
    const existed = (await this.#readIfExists(confPath)) !== undefined

    // 1. Stop the running process. Tolerate non-zero (program may not exist,
    //    may already be stopped). allowNonZero=true so the executor doesn't
    //    throw on supervisor's exit-1 "ERROR (no such process)".
    try {
      await this.#executor.run('supervisorctl', ['stop', name], {
        allowNonZero: true,
      })
    } catch (err) {
      this.#logger.warn(
        { service: name, err },
        'removeService: supervisorctl stop failed (continuing)',
      )
    }

    // 2. Drop from supervisord's in-memory registry. Same tolerance —
    //    "no such process" is fine here too.
    try {
      await this.#executor.run('supervisorctl', ['remove', name], {
        allowNonZero: true,
      })
    } catch (err) {
      this.#logger.warn(
        { service: name, err },
        'removeService: supervisorctl remove failed (continuing)',
      )
    }

    // 3. Delete the conf file. ENOENT is fine — the file may have been
    //    cleaned up by a prior call, or never written.
    try {
      await this.#fs.unlink(confPath)
    } catch (err) {
      const code = (err as NodeJS.ErrnoException | undefined)?.code
      if (code !== 'ENOENT') {
        this.#logger.warn(
          { service: name, confPath, err },
          'removeService: conf unlink failed (continuing)',
        )
      }
    }

    // 4. Commit. reread re-scans the conf dir; update reconciles
    //    supervisord's program table with what reread found. Together they
    //    purge any lingering reference to the now-deleted conf.
    try {
      await this.#executor.run('supervisorctl', ['reread'])
      await this.#executor.run('supervisorctl', ['update'])
    } catch (err) {
      this.#logger.warn(
        { service: name, err },
        'removeService: reread/update failed (continuing)',
      )
    }

    this.#logger.info(
      { service: name, confPath, existed },
      'supervisord service removed',
    )
    return existed
  }

  /**
   * List the supervisord program names currently configured on disk —
   * derived from the `.conf` files in `<confDir>`. Used by the bootstrap-
   * time + delta-apply reconciliation pass to detect orphan confs (services
   * on disk but not in the current spec).
   *
   * Returns an empty array if the conf dir doesn't exist yet (fresh volume).
   * Filenames that don't match the `<name>.conf` shape are skipped silently;
   * those would be operator-dropped files that aren't our responsibility.
   */
  async listConfiguredServiceNames(): Promise<string[]> {
    let entries: string[]
    try {
      entries = await this.#fs.readdir(this.#confDir)
    } catch (err) {
      const code = (err as NodeJS.ErrnoException | undefined)?.code
      if (code === 'ENOENT') return []
      this.#logger.warn(
        { confDir: this.#confDir, err },
        'listConfiguredServiceNames: readdir failed',
      )
      return []
    }
    const names: string[] = []
    for (const entry of entries) {
      if (!entry.endsWith('.conf')) continue
      const name = entry.slice(0, -'.conf'.length)
      if (name.length === 0) continue
      names.push(name)
    }
    return names
  }

  /**
   * Reconcile supervisord's on-disk state with a desired set of service
   * names. Anything currently configured but NOT in the desired set is torn
   * down via {@link removeService}.
   *
   * Returns the list of names actually removed (i.e. names that had a conf
   * on disk before this call). Used by:
   *   - The bootstrap path (StartingServicesStage) to self-heal runtimes
   *     whose volume carries stale confs from a previous spec revision.
   *   - The live delta-apply path (RuntimeSpecApplier) to honour spec edits
   *     that drop a service.
   *
   * Safe to call with an empty `desired` — that's the "spec has zero
   * services" case and means "remove everything".
   */
  async reconcileServices(
    desired: ReadonlySet<string>,
    options?: { preserve?: ReadonlySet<string> },
  ): Promise<string[]> {
    const configured = await this.listConfiguredServiceNames()
    const preserve = options?.preserve
    const orphans = configured.filter(
      (name) => !desired.has(name) && !(preserve?.has(name) ?? false),
    )
    if (orphans.length === 0) {
      this.#logger.debug(
        { configured: configured.length, desired: desired.size },
        'reconcileServices: no orphans',
      )
      return []
    }
    this.#logger.info(
      { orphans, configured, desired: [...desired] },
      'reconcileServices: removing orphan supervisord confs',
    )
    const removed: string[] = []
    for (const name of orphans) {
      const wasPresent = await this.removeService(name)
      if (wasPresent) removed.push(name)
    }
    return removed
  }
}

/**
 * Render a `ServiceSpec` into a supervisord program block. Exported for
 * direct unit testing of the conf-rendering contract — the controller calls
 * this internally.
 *
 * Output shape:
 *
 *     [program:<name>]
 *     command=<command>
 *     user=<user || "agent">
 *     autorestart=<true|false>
 *     stdout_logfile=/var/log/supervisor/<name>.log
 *     stdout_logfile_maxbytes=10MB
 *     stdout_logfile_backups=3
 *     redirect_stderr=true
 *     environment=<KEY1=val1,KEY2="value with spaces">  (omitted if no env)
 *
 * Env-value quoting: supervisord's `environment=` parser breaks on mixed
 * quoted/unquoted pairs when values contain `:`, `/`, `@`, etc. Quote every
 * value so preset toolchain paths and merged secrets compose reliably.
 */
export function renderServiceBlock(spec: ServiceSpec): string {
  // Guard against BOTH undefined and null — same wire-payload caveat as `env`
  // below (server's SignalR serializer can emit `"user": null` for unset
  // optionals). `!= null` (loose) catches both in one check; the previous
  // `!== undefined` check let `null` slip through and crashed on `.length`.
  const user = spec.user != null && spec.user.length > 0 ? spec.user : DEFAULT_USER
  const autorestart = spec.autorestart === false ? 'false' : 'true'

  const lines: string[] = [
    `[program:${spec.name}]`,
    `command=${spec.command}`,
    `user=${user}`,
    `autorestart=${autorestart}`,
    `stdout_logfile=${LOG_DIR}/${spec.name}.log`,
    `stdout_logfile_maxbytes=10MB`,
    `stdout_logfile_backups=3`,
    `redirect_stderr=true`,
  ]

  // Guard against BOTH undefined and null. The server's SignalR
  // PayloadSerializerOptions historically emitted `"env": null` over the wire
  // for unset optionals (fixed on the server side, but the daemon needs to
  // stay resilient since old/replaying payloads can still carry nulls).
  // Using `!= null` (loose equality) catches both undefined and null in one
  // check — `Object.keys(null)` throws "Cannot convert undefined or null to
  // object", which is exactly the failure mode this guards against.
  if (spec.env != null && Object.keys(spec.env).length > 0) {
    lines.push(`environment=${formatEnvironment(spec.env)}`)
  }

  return lines.join('\n') + '\n'
}

/**
 * Format an env map as a supervisord `environment=` value:
 * `KEY1="val1",KEY2="val2"`. Every value is double-quoted; embedded `"` and
 * `\` are backslash-escaped. Percent signs are doubled for supervisord's
 * `%(ENV_X)s` interpolation pass.
 */
function formatEnvironment(env: Record<string, string>): string {
  const parts: string[] = []
  for (const [key, value] of Object.entries(env)) {
    parts.push(`${key}=${quoteEnvValue(value)}`)
  }
  return parts.join(',')
}

function quoteEnvValue(value: string): string {
  const escaped = value
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/%/g, '%%')
  return `"${escaped}"`
}
