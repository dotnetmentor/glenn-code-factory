// CloudflaredController — manages the `cloudflared` supervisord program block
// based on the runtime's `TUNNEL_TOKEN` environment variable.
//
// === Context ===
//
// Phase 4 of the `cloudflare-tunnel-preview` spec. The runtime base image ships
// the `cloudflared` binary at `/usr/local/bin/cloudflared` (see
// /workspace/Dockerfile.runtime-base, layer 1d). When the .NET API provisions a
// runtime machine for a project with a tunnel allocated, it passes three env
// vars on the Fly machine: TUNNEL_TOKEN, PREVIEW_PORT, PREVIEW_HOSTNAME (see
// /workspace/packages/dotnet-api/Source/Features/RuntimeLifecycle/Jobs/
// RuntimeProvisionerJob.cs).
//
// At daemon boot we look at those env vars: if `TUNNEL_TOKEN` is present and
// non-empty, we render a supervisord program block for `cloudflared` into
// `/data/.glenn/supervisor.d/cloudflared.conf` and call `supervisorctl
// reread` + `update` so supervisord picks it up. If TUNNEL_TOKEN is missing
// (legacy branches, projects without a tunnel allocated, or local dev), this
// controller is never invoked.
//
// === Conf shape ===
//
// We bake the literal token into the `command=` line rather than relying on
// supervisord's `environment=` directive — supervisord's `%(ENV_X)s`
// interpolation only reads supervisord's *own* process environment, and the
// daemon receives TUNNEL_TOKEN in its own env, not supervisord's. The
// /data/.glenn volume is non-public per-runtime storage so the token at rest
// on disk has the same sensitivity profile as the token in the env. Fewer
// moving parts than re-exporting the var into the supervisord process.
//
// === Idempotency / permissions ===
//
// Same patterns as SupervisordController (see that module for the deeper
// rationale): byte-identical conf on disk → no-op; differing conf → overwrite +
// reread/update. Production wires a sudo-prefixed IExecutor for supervisorctl.

import type { access, readFile, writeFile } from 'node:fs/promises'
import type { Logger } from 'pino'

import {
  RuntimeEventTypes,
  type RuntimeEventEmitter,
} from '../events/RuntimeEventEmitter.js'

import type { IExecutor } from './IExecutor.js'

/**
 * Subset of `node:fs/promises` we touch. Carved out as an interface so tests
 * can hand-roll a fake without `vi.mock`.
 */
export interface CloudflaredControllerFs {
  readFile: typeof readFile
  writeFile: typeof writeFile
  access: typeof access
}

/**
 * Inputs to render + apply the cloudflared supervisord block. `previewPort`
 * is not currently used in the conf (cloudflared's ingress mapping lives in
 * the tunnel definition on the Cloudflare side, not in the local command) but
 * we keep it on the API for symmetry with the env wire-up and to make future
 * changes (e.g. switching to a CLI flag-driven ingress) a non-breaking edit.
 * `previewHostname` is purely for log breadcrumbs.
 */
export interface CloudflaredConfig {
  /** Cloudflare tunnel token. Embedded literally into the `command=` line. */
  tunnelToken: string
  /** Local port cloudflared forwards to. Kept on the API for future use. */
  previewPort: number
  /** FQDN exposed by the tunnel. Logs only — never used to build the conf. */
  previewHostname?: string
}

const DEFAULT_CONF_DIR = '/data/.glenn/supervisor.d'
const CONF_FILE_NAME = 'cloudflared.conf'
const LOG_DIR = '/var/log/supervisor'
const CLOUDFLARED_BIN = '/usr/local/bin/cloudflared'

/** Default health-check cadence: once per minute. */
const DEFAULT_HEALTH_CHECK_INTERVAL_MS = 60_000
/** Per-check timeout. A tunnel that takes >5s to respond to HEAD is functionally down. */
const DEFAULT_HEALTH_CHECK_TIMEOUT_MS = 5_000

/**
 * Tri-state for the tunnel health-check FSM. `Unknown` is the initial state
 * before the first probe lands; we deliberately do NOT emit a transition
 * event into Unknown so a runtime that starts with a flapping tunnel doesn't
 * spam the Timeline with a synthetic "Down" on boot.
 */
type TunnelHealth = 'Unknown' | 'Up' | 'Down'

/**
 * Minimal HEAD-only HTTP probe surface. Carved out so tests can inject a fake
 * without booting `node:https`. Default implementation uses native fetch.
 */
export interface CloudflaredHealthProbe {
  /**
   * Send a HEAD request to `url`. Resolves with the HTTP status code on any
   * response (including 5xx — a 502 still means cloudflared answered, so the
   * tunnel is up); rejects on network error, DNS failure, or timeout. The
   * `timeoutMs` is the deadline from call to first byte.
   */
  head(url: string, timeoutMs: number): Promise<number>
}

export interface CloudflaredControllerDeps {
  executor: IExecutor
  fs: CloudflaredControllerFs
  logger: Logger
  /** Override the conf-d directory. Production uses `/data/.glenn/supervisor.d`. */
  confDir?: string
  /**
   * Structured RuntimeEvent emitter. When provided, the controller emits
   * `CloudflaredTunnelDown` / `CloudflaredTunnelUp` on health transitions
   * (Up→Down, Down→Up). Optional for back-compat with existing tests.
   */
  emitter?: RuntimeEventEmitter
  /** Override the health-check probe. Defaults to a `fetch`-based HEAD with timeout. */
  healthProbe?: CloudflaredHealthProbe
  /** Override the polling interval. Default 60s. */
  healthCheckIntervalMs?: number
  /** Override the per-probe timeout. Default 5s. */
  healthCheckTimeoutMs?: number
  /** Override the timer scheduler. Defaults to `setInterval` — tests inject a fake. */
  setInterval?: (cb: () => void, ms: number) => unknown
  /** Override the timer cancel. Defaults to `clearInterval`. */
  clearInterval?: (handle: unknown) => void
}

export class CloudflaredController {
  readonly #executor: IExecutor
  readonly #fs: CloudflaredControllerFs
  readonly #logger: Logger
  readonly #confDir: string
  readonly #emitter: RuntimeEventEmitter | undefined
  readonly #healthProbe: CloudflaredHealthProbe
  readonly #healthCheckIntervalMs: number
  readonly #healthCheckTimeoutMs: number
  readonly #setInterval: (cb: () => void, ms: number) => unknown
  readonly #clearInterval: (handle: unknown) => void

  // Health-check FSM state. `#healthState === 'Unknown'` means we haven't
  // recorded a transition yet — first probe outcome promotes us to Up or Down
  // WITHOUT emitting an event (we only emit on transitions between known
  // states). `#healthHandle` is null until `start()` schedules the loop.
  #healthState: TunnelHealth = 'Unknown'
  #healthHandle: unknown = null
  #healthHostname: string | undefined

  constructor(deps: CloudflaredControllerDeps) {
    this.#executor = deps.executor
    this.#fs = deps.fs
    this.#logger = deps.logger.child({ module: 'cloudflared-controller' })
    this.#confDir = deps.confDir ?? DEFAULT_CONF_DIR
    this.#emitter = deps.emitter
    this.#healthProbe = deps.healthProbe ?? defaultHealthProbe
    this.#healthCheckIntervalMs =
      deps.healthCheckIntervalMs ?? DEFAULT_HEALTH_CHECK_INTERVAL_MS
    this.#healthCheckTimeoutMs =
      deps.healthCheckTimeoutMs ?? DEFAULT_HEALTH_CHECK_TIMEOUT_MS
    this.#setInterval = deps.setInterval ?? ((cb, ms) => setInterval(cb, ms))
    this.#clearInterval =
      deps.clearInterval ?? ((handle) => clearInterval(handle as NodeJS.Timeout))
  }

  /**
   * Idempotently install/update the cloudflared supervisord program. Writes
   * `<confDir>/cloudflared.conf` with the rendered block and runs
   * `supervisorctl reread && supervisorctl update` so supervisord picks it up.
   *
   * If the existing file is byte-identical to what we'd render, the write AND
   * the supervisorctl calls are skipped. If it differs (token rotated, drift),
   * we overwrite and reread/update.
   */
  async apply(config: CloudflaredConfig): Promise<void> {
    const confPath = `${this.#confDir}/${CONF_FILE_NAME}`
    const desired = CloudflaredController.render(config)

    const existing = await this.#readIfExists(confPath)
    if (existing === desired) {
      this.#logger.info(
        { confPath, hostname: config.previewHostname },
        'cloudflared conf unchanged, skipping',
      )
      return
    }

    await this.#fs.writeFile(confPath, desired, 'utf8')
    this.#logger.info(
      {
        confPath,
        hostname: config.previewHostname,
        action: existing === undefined ? 'created' : 'updated',
      },
      'cloudflared conf written',
    )

    await this.#executor.run('supervisorctl', ['reread'])
    await this.#executor.run('supervisorctl', ['update'])
    this.#logger.info(
      { hostname: config.previewHostname },
      'cloudflared service registered with supervisord',
    )
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
      return undefined
    }
  }

  /**
   * Render the supervisord program block for cloudflared. Pure function —
   * exported as a static so tests (and the controller itself) can call it
   * without instantiating.
   *
   * Command-line shape: we run the bare `cloudflared tunnel run --token <T>`.
   * Earlier iterations appended `--no-autoupdate --protocol http2`; both are
   * dropped because:
   *   * `--no-autoupdate` is a `tunnel`-command option (must precede `run`),
   *     and on cloudflared 2026.3+ "installed by a package manager" disables
   *     autoupdate automatically — we verified this in the live log:
   *       "cloudflared will not automatically update if installed by a
   *        package manager."
   *   * `--protocol http2` is no longer a recognised flag on the `run`
   *     subcommand. The default protocol is QUIC, which works fine on Fly
   *     machines (verified by inspecting the live runtime's tunnel
   *     registration logs).
   * Appending unknown flags made cloudflared exit with "Incorrect Usage:
   * flag provided but not defined" and supervisord put the program into
   * FATAL after `startretries` exits. Keeping the command surface minimal
   * is the safest forwards-compatible choice.
   *
   * Output shape:
   *
   *   [program:cloudflared]
   *   command=/usr/local/bin/cloudflared tunnel run --token <TOKEN>
   *   directory=/data
   *   autostart=true
   *   autorestart=true
   *   startsecs=5
   *   startretries=3
   *   stopwaitsecs=10
   *   stopsignal=TERM
   *   stdout_logfile=/var/log/supervisor/cloudflared.out.log
   *   stderr_logfile=/var/log/supervisor/cloudflared.err.log
   *   stdout_logfile_maxbytes=10MB
   *   stderr_logfile_maxbytes=10MB
   */
  static render(config: CloudflaredConfig): string {
    const lines: string[] = [
      `[program:cloudflared]`,
      `command=${CLOUDFLARED_BIN} tunnel run --token ${config.tunnelToken}`,
      `directory=/data`,
      `autostart=true`,
      `autorestart=true`,
      `startsecs=5`,
      `startretries=3`,
      `stopwaitsecs=10`,
      `stopsignal=TERM`,
      `stdout_logfile=${LOG_DIR}/cloudflared.out.log`,
      `stderr_logfile=${LOG_DIR}/cloudflared.err.log`,
      `stdout_logfile_maxbytes=10MB`,
      `stderr_logfile_maxbytes=10MB`,
    ]
    return lines.join('\n') + '\n'
  }

  // ============================================================================
  // Health-check FSM (runtime-observability-super-admin)
  // ============================================================================

  /**
   * Start the periodic tunnel health-check. One HTTPS HEAD per minute against
   * `https://<previewHostname>`. The first probe outcome promotes us out of
   * `Unknown` silently — only Up→Down and Down→Up transitions emit events
   * (`CloudflaredTunnelDown`, `CloudflaredTunnelUp`).
   *
   * No-op when:
   *   - `previewHostname` is missing/empty (no tunnel allocated, nothing to probe),
   *   - the loop is already running (double-start is idempotent),
   *   - no emitter was provided (event emission is the whole point — without
   *     it we'd just be spinning the wheel).
   */
  startHealthCheck(previewHostname: string | undefined): void {
    if (this.#healthHandle !== null) return
    if (previewHostname === undefined || previewHostname.trim() === '') {
      this.#logger.debug('no preview hostname; skipping tunnel health-check')
      return
    }
    if (this.#emitter === undefined) {
      this.#logger.debug('no emitter wired; skipping tunnel health-check')
      return
    }
    this.#healthHostname = previewHostname.trim()
    // Schedule, then fire one immediately so we don't sit in `Unknown` for the
    // full interval before the first probe. The immediate probe is best-
    // effort: any failure is logged and resolves to the standard FSM step.
    this.#healthHandle = this.#setInterval(() => {
      this.#runHealthProbe().catch((err: unknown) => {
        this.#logger.debug({ err }, 'tunnel health probe iteration failed')
      })
    }, this.#healthCheckIntervalMs)
    // Eagerly kick the first probe — surfaces an actual Up/Down within a few
    // seconds of boot rather than after the first interval tick.
    this.#runHealthProbe().catch((err: unknown) => {
      this.#logger.debug({ err }, 'initial tunnel health probe failed')
    })
    this.#logger.info(
      { hostname: this.#healthHostname, intervalMs: this.#healthCheckIntervalMs },
      'cloudflared tunnel health-check started',
    )
  }

  /**
   * Stop the periodic health-check (called by ShutdownCoordinator on daemon
   * teardown). Idempotent — calling on a never-started controller is a
   * no-op. Resets the internal FSM so a subsequent `startHealthCheck` begins
   * from `Unknown` again.
   */
  stopHealthCheck(): void {
    if (this.#healthHandle === null) return
    this.#clearInterval(this.#healthHandle)
    this.#healthHandle = null
    this.#healthState = 'Unknown'
    this.#healthHostname = undefined
  }

  /**
   * One probe iteration. Performs a HEAD against `https://<hostname>`, maps
   * the outcome to `Up` (any HTTP response, including 5xx — cloudflared
   * answered, the tunnel is up) or `Down` (network error, DNS failure,
   * timeout), and emits an event only when the state TRANSITIONS between two
   * known states. The first transition out of `Unknown` is silent — it sets
   * the baseline.
   */
  async #runHealthProbe(): Promise<void> {
    const hostname = this.#healthHostname
    if (hostname === undefined) return
    const url = `https://${hostname}`
    let next: 'Up' | 'Down'
    let detail: { statusCode?: number; errorMessage?: string }
    try {
      const status = await this.#healthProbe.head(url, this.#healthCheckTimeoutMs)
      next = 'Up'
      detail = { statusCode: status }
    } catch (err) {
      next = 'Down'
      detail = {
        errorMessage: err instanceof Error ? err.message : String(err),
      }
    }

    const prev = this.#healthState
    this.#healthState = next
    if (prev === 'Unknown') {
      // First probe — set baseline silently. Avoids a synthetic Down on boot
      // when the tunnel hasn't finished registering with Cloudflare yet.
      this.#logger.debug(
        { hostname, state: next, ...detail },
        'tunnel health baseline established',
      )
      return
    }
    if (prev === next) return // no transition

    if (next === 'Down') {
      this.#emitter?.emit(
        RuntimeEventTypes.CloudflaredTunnelDown,
        'Error',
        { hostname, ...detail },
      )
      this.#logger.warn({ hostname, ...detail }, 'cloudflared tunnel transitioned Up -> Down')
    } else {
      this.#emitter?.emit(
        RuntimeEventTypes.CloudflaredTunnelUp,
        'Info',
        { hostname, ...detail },
      )
      this.#logger.info({ hostname, ...detail }, 'cloudflared tunnel transitioned Down -> Up')
    }
  }
}

// ============================================================================
// Default health probe — minimal native-fetch HEAD with a hard timeout.
// ============================================================================

const defaultHealthProbe: CloudflaredHealthProbe = {
  async head(url, timeoutMs) {
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), timeoutMs)
    try {
      const response = await fetch(url, {
        method: 'HEAD',
        signal: controller.signal,
        redirect: 'manual',
      })
      return response.status
    } finally {
      clearTimeout(timer)
    }
  },
}
