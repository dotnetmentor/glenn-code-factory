import { describe, expect, it } from 'vitest'
import { DaemonConfig, DaemonConfigError, type LogLevel } from './DaemonConfig.js'

// --- Test helpers ---

// A real-shape JWT made from three base64url-safe garbage segments. Not a literal real
// token, just three valid base64url chunks separated by dots.
const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const ANOTHER_VALID_TOKEN =
  'AAAAAAAAAAAAAAAAAAAAAA.BBBBBBBBBBBBBBBBBBBBBB.CCCCCCCCCCCCCCCCCCCCCC'

function validEnv(): NodeJS.ProcessEnv {
  return {
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: '11111111-2222-3333-4444-555555555555',
    DAEMON_VERSION: '0.1.0-dev',
  }
}

describe('DaemonConfig.fromEnv', () => {
  describe('all-green env', () => {
    it('populates fields with correct types and applies defaults', () => {
      const config = DaemonConfig.fromEnv(validEnv())

      expect(config.mainApiUrl).toBeInstanceOf(URL)
      expect(config.mainApiUrl.toString()).toBe('http://localhost:5338/')
      expect(config.runtimeId).toBe('11111111-2222-3333-4444-555555555555')
      expect(config.daemonVersion).toBe('0.1.0-dev')
      expect(config.runtimeToken).toBe(VALID_TOKEN)

      // Defaults
      expect(config.logLevel).toBe('info')
      expect(typeof config.quietTimeoutMs).toBe('number')
      expect(config.quietTimeoutMs).toBe(300_000)
      expect(config.heartbeatIntervalMs).toBe(5_000)
      expect(config.turnTimeoutMs).toBe(60_000)
    })

    it('accepts https://', () => {
      const env = validEnv()
      env['MAIN_API_URL'] = 'https://api.example.com'
      const config = DaemonConfig.fromEnv(env)
      expect(config.mainApiUrl.protocol).toBe('https:')
    })

    it('accepts production-ish stable version like 1.2.3', () => {
      const env = validEnv()
      env['DAEMON_VERSION'] = '1.2.3'
      expect(() => DaemonConfig.fromEnv(env)).not.toThrow()
    })
  })

  describe('required vars missing in turn', () => {
    const required = [
      'GLENN_RUNTIME_TOKEN',
      'MAIN_API_URL',
      'RUNTIME_ID',
      'DAEMON_VERSION',
    ] as const

    for (const varName of required) {
      it(`throws DaemonConfigError when ${varName} is missing`, () => {
        const env = validEnv()
        delete env[varName]
        let caught: DaemonConfigError | null = null
        try {
          DaemonConfig.fromEnv(env)
        } catch (e) {
          caught = e as DaemonConfigError
        }
        expect(caught).toBeInstanceOf(DaemonConfigError)
        expect(caught!.problems).toContain(`${varName} is required`)
      })

      it(`throws DaemonConfigError when ${varName} is empty string`, () => {
        const env = validEnv()
        env[varName] = ''
        expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
      })
    }
  })

  describe('multiple required missing', () => {
    it('lists all missing vars in a single DaemonConfigError', () => {
      const env: NodeJS.ProcessEnv = {}
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.problems).toEqual(
        expect.arrayContaining([
          'GLENN_RUNTIME_TOKEN is required',
          'MAIN_API_URL is required',
          'RUNTIME_ID is required',
          'DAEMON_VERSION is required',
        ]),
      )
    })
  })

  describe('invalid MAIN_API_URL', () => {
    it('throws on non-URL string', () => {
      const env = validEnv()
      env['MAIN_API_URL'] = 'not a url'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on file:// scheme', () => {
      const env = validEnv()
      env['MAIN_API_URL'] = 'file:///etc/passwd'
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.problems.some((p) => p.includes('MAIN_API_URL'))).toBe(true)
    })
  })

  describe('invalid RUNTIME_ID', () => {
    it('throws on non-UUID string', () => {
      const env = validEnv()
      env['RUNTIME_ID'] = 'not-a-uuid'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on a UUID-shaped string with bad chars', () => {
      const env = validEnv()
      env['RUNTIME_ID'] = '11111111-2222-3333-4444-zzzzzzzzzzzz'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })
  })

  describe('token validation — segment count', () => {
    for (const [label, badToken] of [
      ['1 segment', 'onlyonesegment'],
      ['2 segments', 'header.payload'],
      ['4 segments', 'a.b.c.d'],
    ] as const) {
      it(`throws on token with ${label} without leaking the value`, () => {
        const env = validEnv()
        env['GLENN_RUNTIME_TOKEN'] = badToken
        let caught: DaemonConfigError | null = null
        try {
          DaemonConfig.fromEnv(env)
        } catch (e) {
          caught = e as DaemonConfigError
        }
        expect(caught).toBeInstanceOf(DaemonConfigError)
        // Belt-and-braces redaction.
        expect(caught!.message).not.toContain(badToken)
        expect(caught!.problems.join(' ')).not.toContain(badToken)
      })
    }
  })

  describe('token validation — invalid base64url chars', () => {
    it('throws when a segment contains "?" without leaking the value', () => {
      const badToken = 'header.pay?load.signature'
      const env = validEnv()
      env['GLENN_RUNTIME_TOKEN'] = badToken
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.message).not.toContain(badToken)
      expect(caught!.problems.join(' ')).not.toContain(badToken)
    })

    it('throws when a segment contains "+" (standard base64, not base64url)', () => {
      const badToken = 'header.pay+load.signature'
      const env = validEnv()
      env['GLENN_RUNTIME_TOKEN'] = badToken
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws when a segment is empty (e.g. "a..b")', () => {
      const badToken = 'header..signature'
      const env = validEnv()
      env['GLENN_RUNTIME_TOKEN'] = badToken
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.message).not.toContain(badToken)
    })
  })

  describe('optional defaults', () => {
    it('applies all defaults when optional vars are absent', () => {
      const config = DaemonConfig.fromEnv(validEnv())
      expect(config.logLevel).toBe('info')
      expect(config.quietTimeoutMs).toBe(300_000)
      expect(config.heartbeatIntervalMs).toBe(5_000)
      expect(config.turnTimeoutMs).toBe(60_000)
      expect(config.processKillEscalationMs).toBe(10_000)
    })

    it('parses DAEMON_PROCESS_KILL_ESCALATION_MS=15000 → 15_000', () => {
      const env = validEnv()
      env['DAEMON_PROCESS_KILL_ESCALATION_MS'] = '15000'
      const config = DaemonConfig.fromEnv(env)
      expect(config.processKillEscalationMs).toBe(15_000)
    })

    it('parses DAEMON_QUIET_TIMEOUT_MS=120000 → 120_000', () => {
      const env = validEnv()
      env['DAEMON_QUIET_TIMEOUT_MS'] = '120000'
      const config = DaemonConfig.fromEnv(env)
      expect(config.quietTimeoutMs).toBe(120_000)
    })

    it('parses DAEMON_HEARTBEAT_INTERVAL_MS=10000 → 10_000', () => {
      const env = validEnv()
      env['DAEMON_HEARTBEAT_INTERVAL_MS'] = '10000'
      const config = DaemonConfig.fromEnv(env)
      expect(config.heartbeatIntervalMs).toBe(10_000)
    })

    it('parses DAEMON_TURN_TIMEOUT_MS=300000 → 300_000', () => {
      const env = validEnv()
      env['DAEMON_TURN_TIMEOUT_MS'] = '300000'
      const config = DaemonConfig.fromEnv(env)
      expect(config.turnTimeoutMs).toBe(300_000)
    })

    it('accepts each valid log level', () => {
      const levels: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'fatal']
      for (const level of levels) {
        const env = validEnv()
        env['DAEMON_LOG_LEVEL'] = level
        const config = DaemonConfig.fromEnv(env)
        expect(config.logLevel).toBe(level)
      }
    })
  })

  describe('out-of-range numerics', () => {
    it('throws on DAEMON_HEARTBEAT_INTERVAL_MS=500 (sub-second)', () => {
      const env = validEnv()
      env['DAEMON_HEARTBEAT_INTERVAL_MS'] = '500'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_HEARTBEAT_INTERVAL_MS=0', () => {
      const env = validEnv()
      env['DAEMON_HEARTBEAT_INTERVAL_MS'] = '0'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_HEARTBEAT_INTERVAL_MS=70000 (above ceiling)', () => {
      const env = validEnv()
      env['DAEMON_HEARTBEAT_INTERVAL_MS'] = '70000'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_QUIET_TIMEOUT_MS=0', () => {
      const env = validEnv()
      env['DAEMON_QUIET_TIMEOUT_MS'] = '0'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_TURN_TIMEOUT_MS above 600_000', () => {
      const env = validEnv()
      env['DAEMON_TURN_TIMEOUT_MS'] = '600001'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_PROCESS_KILL_ESCALATION_MS=0 (must be positive)', () => {
      const env = validEnv()
      env['DAEMON_PROCESS_KILL_ESCALATION_MS'] = '0'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_PROCESS_KILL_ESCALATION_MS above ceiling', () => {
      const env = validEnv()
      env['DAEMON_PROCESS_KILL_ESCALATION_MS'] = `${5 * 60_000 + 1}`
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })
  })

  describe('unparseable numerics', () => {
    it('throws on DAEMON_QUIET_TIMEOUT_MS=abc', () => {
      const env = validEnv()
      env['DAEMON_QUIET_TIMEOUT_MS'] = 'abc'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_HEARTBEAT_INTERVAL_MS=12.5 (non-integer)', () => {
      const env = validEnv()
      env['DAEMON_HEARTBEAT_INTERVAL_MS'] = '12.5'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })

    it('throws on DAEMON_TURN_TIMEOUT_MS=123abc (parseInt footgun)', () => {
      const env = validEnv()
      env['DAEMON_TURN_TIMEOUT_MS'] = '123abc'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })
  })

  describe('bad log level', () => {
    it('throws on DAEMON_LOG_LEVEL=verbose', () => {
      const env = validEnv()
      env['DAEMON_LOG_LEVEL'] = 'verbose'
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.problems.some((p) => p.includes('DAEMON_LOG_LEVEL'))).toBe(true)
    })
  })

  describe('invalid DAEMON_VERSION', () => {
    it('throws on a non-semver-shaped value', () => {
      const env = validEnv()
      env['DAEMON_VERSION'] = 'not-a-version'
      expect(() => DaemonConfig.fromEnv(env)).toThrow(DaemonConfigError)
    })
  })

  describe('DAEMON_ENV_FILE_PATH (Spec 14 Card 7)', () => {
    it('defaults to /data/.glenn/env when unset', () => {
      const config = DaemonConfig.fromEnv(validEnv())
      expect(config.envFilePath).toBe('/data/.glenn/env')
    })

    it('accepts a custom absolute override', () => {
      const env = validEnv()
      env['DAEMON_ENV_FILE_PATH'] = '/tmp/glenn-env-custom'
      const config = DaemonConfig.fromEnv(env)
      expect(config.envFilePath).toBe('/tmp/glenn-env-custom')
    })

    it('throws DaemonConfigError on a relative path', () => {
      const env = validEnv()
      env['DAEMON_ENV_FILE_PATH'] = 'relative/path/env'
      let caught: DaemonConfigError | null = null
      try {
        DaemonConfig.fromEnv(env)
      } catch (e) {
        caught = e as DaemonConfigError
      }
      expect(caught).toBeInstanceOf(DaemonConfigError)
      expect(caught!.problems.some((p) => p.includes('DAEMON_ENV_FILE_PATH'))).toBe(true)
    })
  })
})

describe('DaemonConfig.rotateToken', () => {
  it('happy path: replaces the token', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    expect(config.runtimeToken).toBe(VALID_TOKEN)

    config.rotateToken(ANOTHER_VALID_TOKEN)
    expect(config.runtimeToken).toBe(ANOTHER_VALID_TOKEN)
  })

  it('rejects malformed token and retains the original', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const badToken = 'only.two'
    expect(() => config.rotateToken(badToken)).toThrow(DaemonConfigError)
    expect(config.runtimeToken).toBe(VALID_TOKEN) // unchanged
  })

  it('does not echo the bad token in the thrown error', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const badToken = 'one.two.three.four' // wrong segment count
    let caught: DaemonConfigError | null = null
    try {
      config.rotateToken(badToken)
    } catch (e) {
      caught = e as DaemonConfigError
    }
    expect(caught).toBeInstanceOf(DaemonConfigError)
    expect(caught!.message).not.toContain(badToken)
    expect(caught!.problems.join(' ')).not.toContain(badToken)
  })

  it('does not echo a base64url-invalid token in the thrown error', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const badToken = 'aaa.b?b.ccc'
    let caught: DaemonConfigError | null = null
    try {
      config.rotateToken(badToken)
    } catch (e) {
      caught = e as DaemonConfigError
    }
    expect(caught).toBeInstanceOf(DaemonConfigError)
    expect(caught!.message).not.toContain(badToken)
  })
})

describe('DaemonConfig redaction', () => {
  it('toJSON redacts the token', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const serialized = JSON.stringify(config)
    expect(serialized).not.toContain(VALID_TOKEN)
    expect(serialized).toContain('***REDACTED***')
  })

  it('JSON.stringify (which calls toJSON) does not leak the token', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const obj = JSON.parse(JSON.stringify(config)) as Record<string, unknown>
    expect(obj['runtimeToken']).toBe('***REDACTED***')
    // Also: other fields survive the round-trip.
    expect(obj['runtimeId']).toBe('11111111-2222-3333-4444-555555555555')
    expect(obj['daemonVersion']).toBe('0.1.0-dev')
    expect(obj['logLevel']).toBe('info')
  })

  it('util.inspect.custom symbol redacts the token (console.log path)', async () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const util = await import('node:util')
    const inspected = util.inspect(config)
    expect(inspected).not.toContain(VALID_TOKEN)
    expect(inspected).toContain('***REDACTED***')
  })

  it('does not expose the token via Object.keys (real ECMAScript private)', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    const keys = Object.keys(config)
    expect(keys).not.toContain('#runtimeToken')
    expect(keys).not.toContain('runtimeToken') // it's a getter, not an own property
  })

  it('runtimeToken getter still returns the live value', () => {
    const config = DaemonConfig.fromEnv(validEnv())
    expect(config.runtimeToken).toBe(VALID_TOKEN)
  })
})
