// DaemonConfig — loads + validates the env vars supervisord injects into the in-Machine
// daemon process. Single concrete class; no interface, no DI container, no builder.
//
// Construction is gated by the static `fromEnv` factory: the constructor is private so
// production code can't accidentally bypass validation, and tests can't construct
// half-validated instances. All validation problems are collected before throwing so
// operators see every issue in one shot instead of fix-one-thing-redeploy-find-next.

import path from 'node:path'

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'fatal'

const LOG_LEVELS: readonly LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'fatal']

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
// Permissive semver-ish: MAJOR.MINOR.PATCH with optional pre-release suffix.
const VERSION_REGEX = /^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$/
const BASE64URL_SEGMENT_REGEX = /^[A-Za-z0-9_-]+$/

const DEFAULT_LOG_LEVEL: LogLevel = 'info'
const DEFAULT_QUIET_TIMEOUT_MS = 300_000 // 5 minutes
const DEFAULT_HEARTBEAT_INTERVAL_MS = 5_000
const DEFAULT_TURN_TIMEOUT_MS = 60_000
const DEFAULT_PUSH_RETRY_INTERVAL_MS = 30_000 // 30s when actively retrying
const DEFAULT_PUSH_RETRY_QUIET_INTERVAL_MS = 300_000 // 5 minutes during quiet mode
// Spec 13 Card 10: SIGTERM → SIGKILL escalation grace period for hook + git
// child processes. Default mirrors the spec brief; ceiling is 5 minutes (above
// that, the daemon's own shutdown drain is the wrong layer to be waiting in).
const DEFAULT_PROCESS_KILL_ESCALATION_MS = 10_000
const PROCESS_KILL_ESCALATION_MAX_MS = 5 * 60_000

// Spec 14 Card 7: env-var snapshot file path. Default mirrors the canonical
// runtime data layout `/data/.glenn/env`. Operators can override via
// `DAEMON_ENV_FILE_PATH` (e.g. for an alternate mount or a dev container that
// doesn't have `/data` available).
const DEFAULT_ENV_FILE_PATH = '/data/.glenn/env'

const ONE_DAY_MS = 24 * 60 * 60 * 1000
const HEARTBEAT_MIN_MS = 1_000
const HEARTBEAT_MAX_MS = 60_000
const TURN_MAX_MS = 600_000 // 10-minute ceiling for graceful drain

const REDACTED = '***REDACTED***'

export class DaemonConfigError extends Error {
  readonly problems: readonly string[]
  constructor(problems: readonly string[]) {
    super(`DaemonConfig validation failed:\n  - ${problems.join('\n  - ')}`)
    this.name = 'DaemonConfigError'
    this.problems = problems
  }
}

interface DaemonConfigInit {
  mainApiUrl: URL
  runtimeId: string
  daemonVersion: string
  runtimeToken: string
  logLevel: LogLevel
  quietTimeoutMs: number
  heartbeatIntervalMs: number
  turnTimeoutMs: number
  pushRetryIntervalMs: number
  pushRetryQuietIntervalMs: number
  processKillEscalationMs: number
  envFilePath: string
}

export class DaemonConfig {
  // Required
  readonly mainApiUrl: URL
  readonly runtimeId: string
  readonly daemonVersion: string

  // Optional with defaults
  readonly logLevel: LogLevel
  readonly quietTimeoutMs: number
  readonly heartbeatIntervalMs: number
  readonly turnTimeoutMs: number
  readonly pushRetryIntervalMs: number
  readonly pushRetryQuietIntervalMs: number
  readonly processKillEscalationMs: number
  readonly envFilePath: string

  // Token is mutable so UpdateConfig from main API can rotate it without tearing down
  // the SignalR connection. SignalRClient's accessTokenFactory re-reads on every
  // reconnect, so a rotateToken() call followed by an organic reconnect picks up the
  // new token automatically.
  //
  // Uses real ECMAScript private (#) — survives JSON.stringify (it's not enumerable,
  // and we override toJSON anyway), doesn't leak via Object.keys, and isn't stripped
  // away to a public field at compile time the way TypeScript's `private` keyword is.
  #runtimeToken: string

  private constructor(init: DaemonConfigInit) {
    this.mainApiUrl = init.mainApiUrl
    this.runtimeId = init.runtimeId
    this.daemonVersion = init.daemonVersion
    this.logLevel = init.logLevel
    this.quietTimeoutMs = init.quietTimeoutMs
    this.heartbeatIntervalMs = init.heartbeatIntervalMs
    this.turnTimeoutMs = init.turnTimeoutMs
    this.pushRetryIntervalMs = init.pushRetryIntervalMs
    this.pushRetryQuietIntervalMs = init.pushRetryQuietIntervalMs
    this.processKillEscalationMs = init.processKillEscalationMs
    this.envFilePath = init.envFilePath
    this.#runtimeToken = init.runtimeToken
  }

  get runtimeToken(): string {
    return this.#runtimeToken
  }

  rotateToken(newToken: string): void {
    // Validate the new token shape; throw if malformed (do NOT silently keep the old
    // — caller's responsibility to handle the error explicitly).
    const err = validateJwtShape(newToken)
    if (err !== null) {
      throw new DaemonConfigError([`rotateToken rejected: ${err}`])
    }
    this.#runtimeToken = newToken
  }

  // Redact the token in any JSON serialisation path. `console.log` on Node calls
  // util.inspect which prefers the inspect symbol below, but tools like pino, JSON
  // log shippers, and explicit JSON.stringify will hit toJSON.
  toJSON(): Record<string, unknown> {
    return {
      mainApiUrl: this.mainApiUrl.toString(),
      runtimeId: this.runtimeId,
      daemonVersion: this.daemonVersion,
      logLevel: this.logLevel,
      quietTimeoutMs: this.quietTimeoutMs,
      heartbeatIntervalMs: this.heartbeatIntervalMs,
      turnTimeoutMs: this.turnTimeoutMs,
      pushRetryIntervalMs: this.pushRetryIntervalMs,
      pushRetryQuietIntervalMs: this.pushRetryQuietIntervalMs,
      processKillEscalationMs: this.processKillEscalationMs,
      envFilePath: this.envFilePath,
      runtimeToken: REDACTED,
    }
  }

  // Belt-and-braces: console.log in Node uses util.inspect, which checks this symbol
  // first. Returning the redacted JSON shape keeps logs from leaking the token.
  [Symbol.for('nodejs.util.inspect.custom')](): Record<string, unknown> {
    return this.toJSON()
  }

  static fromEnv(env: NodeJS.ProcessEnv = process.env): DaemonConfig {
    // Collect ALL problems before throwing — operators see everything at once.
    const problems: string[] = []

    // --- Required: GLENN_RUNTIME_TOKEN ---
    let runtimeToken = ''
    const rawToken = env['GLENN_RUNTIME_TOKEN']
    if (rawToken === undefined || rawToken === '') {
      problems.push('GLENN_RUNTIME_TOKEN is required')
    } else {
      const tokenErr = validateJwtShape(rawToken)
      if (tokenErr !== null) {
        problems.push(`GLENN_RUNTIME_TOKEN is invalid: ${tokenErr}`)
      } else {
        runtimeToken = rawToken
      }
    }

    // --- Required: MAIN_API_URL ---
    let mainApiUrl: URL | null = null
    const rawUrl = env['MAIN_API_URL']
    if (rawUrl === undefined || rawUrl === '') {
      problems.push('MAIN_API_URL is required')
    } else {
      try {
        const parsed = new URL(rawUrl)
        if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
          problems.push(
            `MAIN_API_URL is invalid: protocol must be http: or https:, got ${parsed.protocol}`,
          )
        } else {
          mainApiUrl = parsed
        }
      } catch {
        problems.push(`MAIN_API_URL is invalid: not a parseable URL`)
      }
    }

    // --- Required: RUNTIME_ID ---
    let runtimeId = ''
    const rawRuntimeId = env['RUNTIME_ID']
    if (rawRuntimeId === undefined || rawRuntimeId === '') {
      problems.push('RUNTIME_ID is required')
    } else if (!UUID_REGEX.test(rawRuntimeId)) {
      problems.push('RUNTIME_ID is invalid: not a canonical UUID')
    } else {
      runtimeId = rawRuntimeId
    }

    // --- Required: DAEMON_VERSION ---
    let daemonVersion = ''
    const rawVersion = env['DAEMON_VERSION']
    if (rawVersion === undefined || rawVersion === '') {
      problems.push('DAEMON_VERSION is required')
    } else if (!VERSION_REGEX.test(rawVersion)) {
      problems.push('DAEMON_VERSION is invalid: must match MAJOR.MINOR.PATCH[-prerelease]')
    } else {
      daemonVersion = rawVersion
    }

    // --- Optional: DAEMON_LOG_LEVEL ---
    let logLevel: LogLevel = DEFAULT_LOG_LEVEL
    const rawLogLevel = env['DAEMON_LOG_LEVEL']
    if (rawLogLevel !== undefined && rawLogLevel !== '') {
      if (!isLogLevel(rawLogLevel)) {
        problems.push(
          `DAEMON_LOG_LEVEL is invalid: must be one of ${LOG_LEVELS.join('|')}, got ${rawLogLevel}`,
        )
      } else {
        logLevel = rawLogLevel
      }
    }

    // --- Optional numerics ---
    const quietTimeoutMs = parseNumeric(
      env['DAEMON_QUIET_TIMEOUT_MS'],
      'DAEMON_QUIET_TIMEOUT_MS',
      DEFAULT_QUIET_TIMEOUT_MS,
      { min: 1, max: ONE_DAY_MS - 1, minLabel: '> 0', maxLabel: '< 24h' },
      problems,
    )

    const heartbeatIntervalMs = parseNumeric(
      env['DAEMON_HEARTBEAT_INTERVAL_MS'],
      'DAEMON_HEARTBEAT_INTERVAL_MS',
      DEFAULT_HEARTBEAT_INTERVAL_MS,
      {
        min: HEARTBEAT_MIN_MS,
        max: HEARTBEAT_MAX_MS,
        minLabel: `>= ${HEARTBEAT_MIN_MS}`,
        maxLabel: `<= ${HEARTBEAT_MAX_MS}`,
      },
      problems,
    )

    const turnTimeoutMs = parseNumeric(
      env['DAEMON_TURN_TIMEOUT_MS'],
      'DAEMON_TURN_TIMEOUT_MS',
      DEFAULT_TURN_TIMEOUT_MS,
      { min: 1, max: TURN_MAX_MS, minLabel: '> 0', maxLabel: `<= ${TURN_MAX_MS}` },
      problems,
    )

    // Push-retry interval bounds: lower bound is 1s (anything tighter is a
    // hot loop hammering the remote). Upper bound is 1 day so a misconfigured
    // huge value is caught — plenty of headroom past the 5-min default for
    // operators who want very lazy retry.
    const pushRetryIntervalMs = parseNumeric(
      env['DAEMON_PUSH_RETRY_INTERVAL_MS'],
      'DAEMON_PUSH_RETRY_INTERVAL_MS',
      DEFAULT_PUSH_RETRY_INTERVAL_MS,
      { min: 1_000, max: ONE_DAY_MS - 1, minLabel: '>= 1000', maxLabel: '< 24h' },
      problems,
    )

    const pushRetryQuietIntervalMs = parseNumeric(
      env['DAEMON_PUSH_RETRY_QUIET_INTERVAL_MS'],
      'DAEMON_PUSH_RETRY_QUIET_INTERVAL_MS',
      DEFAULT_PUSH_RETRY_QUIET_INTERVAL_MS,
      { min: 1_000, max: ONE_DAY_MS - 1, minLabel: '>= 1000', maxLabel: '< 24h' },
      problems,
    )

    // Spec 13 Card 10: SIGTERM → SIGKILL escalation grace. Lower bound is 1ms
    // (sub-1ms is a nonsense config; the timer is best-effort regardless).
    // Upper bound is 5 minutes — anything longer than that and the daemon's
    // shutdown drain has already moved on without us.
    const processKillEscalationMs = parseNumeric(
      env['DAEMON_PROCESS_KILL_ESCALATION_MS'],
      'DAEMON_PROCESS_KILL_ESCALATION_MS',
      DEFAULT_PROCESS_KILL_ESCALATION_MS,
      {
        min: 1,
        max: PROCESS_KILL_ESCALATION_MAX_MS,
        minLabel: '> 0',
        maxLabel: `<= ${PROCESS_KILL_ESCALATION_MAX_MS}`,
      },
      problems,
    )

    // Spec 14 Card 7: env-var file path. Must be absolute — anything else is a
    // configuration error (we don't want a mis-set relative path resolving
    // against whatever cwd supervisord happens to launch the daemon in).
    let envFilePath: string = DEFAULT_ENV_FILE_PATH
    const rawEnvFilePath = env['DAEMON_ENV_FILE_PATH']
    if (rawEnvFilePath !== undefined && rawEnvFilePath !== '') {
      if (!path.isAbsolute(rawEnvFilePath)) {
        problems.push(
          `DAEMON_ENV_FILE_PATH is invalid: must be an absolute path, got ${rawEnvFilePath}`,
        )
      } else {
        envFilePath = rawEnvFilePath
      }
    }

    if (problems.length > 0) {
      throw new DaemonConfigError(problems)
    }

    // Non-null assertions are safe here: if any required value failed validation we
    // would have thrown above. TypeScript can't see that flow, hence the bangs.
    return new DaemonConfig({
      mainApiUrl: mainApiUrl!,
      runtimeId,
      daemonVersion,
      runtimeToken,
      logLevel,
      quietTimeoutMs,
      heartbeatIntervalMs,
      turnTimeoutMs,
      pushRetryIntervalMs,
      pushRetryQuietIntervalMs,
      processKillEscalationMs,
      envFilePath,
    })
  }
}

// --- Helpers ---

// Validate JWT shape WITHOUT echoing the token value. Error messages must only describe
// the *shape* of the failure (segment count, which segment is bad) — never embed the
// token itself, since these messages flow into logs and exception stacks.
function validateJwtShape(token: string): string | null {
  if (token === '') return 'empty token'
  const segments = token.split('.')
  if (segments.length !== 3) {
    return `wrong segment count: ${segments.length}`
  }
  for (let i = 0; i < segments.length; i++) {
    const seg = segments[i]
    if (seg === undefined || seg === '' || !BASE64URL_SEGMENT_REGEX.test(seg)) {
      return `invalid base64url in segment ${i + 1}`
    }
  }
  return null
}

function isLogLevel(value: string): value is LogLevel {
  return (LOG_LEVELS as readonly string[]).includes(value)
}

interface NumericRange {
  min: number
  max: number
  minLabel: string
  maxLabel: string
}

function parseNumeric(
  raw: string | undefined,
  varName: string,
  defaultValue: number,
  range: NumericRange,
  problems: string[],
): number {
  if (raw === undefined || raw === '') {
    return defaultValue
  }
  // Reject anything that isn't a clean integer — `parseInt('123abc')` returns 123,
  // which is a footgun. Require the entire string to be digits (with optional sign).
  if (!/^-?\d+$/.test(raw)) {
    problems.push(`${varName} is invalid: not an integer`)
    return defaultValue
  }
  const parsed = Number.parseInt(raw, 10)
  if (Number.isNaN(parsed)) {
    problems.push(`${varName} is invalid: not an integer`)
    return defaultValue
  }
  if (parsed < range.min || parsed > range.max) {
    problems.push(`${varName} is invalid: must be ${range.minLabel} and ${range.maxLabel}`)
    return defaultValue
  }
  return parsed
}
