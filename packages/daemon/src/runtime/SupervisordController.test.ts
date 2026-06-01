// Tests for SupervisordController (V2). Hand-rolled fake fs + executor — no
// real disk IO, no real supervisorctl. Exercises both the conf-renderer
// (renderServiceBlock) and the controller's idempotency / write / reread
// behaviour against ServiceSpec inputs.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { ExecOpts, ExecResult, IExecutor } from './IExecutor.js'
import {
  SupervisordController,
  renderServiceBlock,
  type ServiceSpec,
  type SupervisordControllerFs,
} from './SupervisordController.js'

// ============================================================================
// Test helpers
// ============================================================================

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log
}

function makeExecutor() {
  const calls: Array<{ command: string; args: readonly string[]; opts?: ExecOpts }> = []
  const run = vi.fn(
    async (
      command: string,
      args: readonly string[],
      opts?: ExecOpts,
    ): Promise<ExecResult> => {
      calls.push({ command, args, ...(opts !== undefined ? { opts } : {}) })
      return { stdout: '', stderr: '', exitCode: 0 }
    },
  )
  const executor: IExecutor = { run }
  return { executor, run, calls }
}

/** In-memory fs fake that mirrors the subset SupervisordController touches. */
function makeFs(initialFiles: Record<string, string> = {}): {
  fs: SupervisordControllerFs
  files: Map<string, string>
  writeFile: ReturnType<typeof vi.fn>
  access: ReturnType<typeof vi.fn>
  readFile: ReturnType<typeof vi.fn>
  readdir: ReturnType<typeof vi.fn>
  unlink: ReturnType<typeof vi.fn>
} {
  const files = new Map<string, string>(Object.entries(initialFiles))
  const writeFile = vi.fn(async (path: unknown, data: unknown) => {
    files.set(String(path), typeof data === 'string' ? data : String(data))
  })
  const access = vi.fn(async (path: unknown) => {
    if (!files.has(String(path))) {
      const err = new Error(`ENOENT: ${String(path)}`)
      ;(err as NodeJS.ErrnoException).code = 'ENOENT'
      throw err
    }
  })
  const readFile = vi.fn(async (path: unknown) => {
    const v = files.get(String(path))
    if (v === undefined) throw new Error(`ENOENT: ${String(path)}`)
    return v
  })
  const readdir = vi.fn(async (dir: unknown) => {
    const prefix = String(dir).replace(/\/$/, '') + '/'
    const entries: string[] = []
    for (const path of files.keys()) {
      if (!path.startsWith(prefix)) continue
      const rest = path.slice(prefix.length)
      // Only direct children (no nested paths through /)
      if (rest.includes('/')) continue
      entries.push(rest)
    }
    return entries
  })
  const unlink = vi.fn(async (path: unknown) => {
    const key = String(path)
    if (!files.has(key)) {
      const err = new Error(`ENOENT: ${key}`)
      ;(err as NodeJS.ErrnoException).code = 'ENOENT'
      throw err
    }
    files.delete(key)
  })
  return {
    fs: {
      writeFile: writeFile as unknown as SupervisordControllerFs['writeFile'],
      access: access as unknown as SupervisordControllerFs['access'],
      readFile: readFile as unknown as SupervisordControllerFs['readFile'],
      readdir: readdir as unknown as SupervisordControllerFs['readdir'],
      unlink: unlink as unknown as SupervisordControllerFs['unlink'],
    },
    files,
    writeFile,
    access,
    readFile,
    readdir,
    unlink,
  }
}

const CONF_DIR = '/etc/supervisor/conf.d'

function makeController(opts: {
  executor: IExecutor
  fs: SupervisordControllerFs
  confDir?: string
}) {
  return new SupervisordController({
    executor: opts.executor,
    fs: opts.fs,
    logger: makeLogger() as unknown as Logger,
    ...(opts.confDir !== undefined ? { confDir: opts.confDir } : {}),
  })
}

const REDIS_SPEC: ServiceSpec = {
  name: 'redis',
  command: '/usr/bin/redis-server --port 6379',
  autorestart: true,
}

const POSTGRES_SPEC: ServiceSpec = {
  name: 'postgres',
  command: '/usr/lib/postgresql/15/bin/postgres -D /var/lib/postgresql/data',
  user: 'postgres',
  autorestart: true,
}

// ============================================================================
// renderServiceBlock — pure function tests
// ============================================================================

describe('renderServiceBlock', () => {
  it('renders the minimal required fields with sensible defaults', () => {
    const block = renderServiceBlock({
      name: 'foo',
      command: '/usr/bin/foo',
    })
    expect(block).toBe(
      [
        '[program:foo]',
        'command=/usr/bin/foo',
        'user=agent',
        'autorestart=true',
        'stdout_logfile=/var/log/supervisor/foo.log',
        'stdout_logfile_maxbytes=10MB',
        'stdout_logfile_backups=3',
        'redirect_stderr=true',
      ].join('\n') + '\n',
    )
  })

  it('honours user override when provided', () => {
    const block = renderServiceBlock({
      name: 'postgres',
      command: 'postgres',
      user: 'postgres',
    })
    expect(block).toContain('user=postgres')
    expect(block).not.toContain('user=agent')
  })

  // Regression: the C# SignalR PayloadSerializerOptions can emit
  // `"user": null` for unset optionals (same gotcha already documented for
  // `env` below). The previous `spec.user !== undefined` check let null
  // through and crashed on `.length` — fall back to DEFAULT_USER instead.
  it('falls back to DEFAULT_USER when user is null (wire-payload safety)', () => {
    const block = renderServiceBlock({
      name: 'svc',
      command: '/usr/bin/svc',
      // Simulate the over-the-wire null that bit the agent-template E2E test.
      user: null as unknown as string | undefined,
    })
    expect(block).toContain('user=agent')
  })

  it('renders autorestart=false when explicitly set false', () => {
    const block = renderServiceBlock({
      name: 'one-shot',
      command: '/usr/bin/migrate',
      autorestart: false,
    })
    expect(block).toContain('autorestart=false')
  })

  it('omits the environment line when env is undefined or empty', () => {
    const withoutEnv = renderServiceBlock({ name: 'a', command: 'a' })
    expect(withoutEnv).not.toContain('environment=')

    const withEmptyEnv = renderServiceBlock({ name: 'b', command: 'b', env: {} })
    expect(withEmptyEnv).not.toContain('environment=')
  })

  it('renders environment values, quoting only when unsafe characters appear', () => {
    const block = renderServiceBlock({
      name: 'minio',
      command: '/minio',
      env: {
        MINIO_ROOT_USER: 'minioadmin', // safe — no quotes
        MINIO_ROOT_PASSWORD: 'pa ss', // contains space — quoted
      },
    })
    expect(block).toContain(
      'environment=MINIO_ROOT_USER=minioadmin,MINIO_ROOT_PASSWORD="pa ss"',
    )
  })

  it('escapes embedded backslashes and quotes in env values', () => {
    const block = renderServiceBlock({
      name: 'x',
      command: 'x',
      env: { WEIRD: 'he said "hi"\\back' },
    })
    expect(block).toContain('environment=WEIRD="he said \\"hi\\"\\\\back"')
  })

  it('does NOT render the healthcheck into the supervisord conf', () => {
    // Healthcheck is consumed by the daemon's health-poller, not supervisord
    // (supervisord has no native healthcheck primitive).
    const block = renderServiceBlock({
      name: 'redis',
      command: 'redis-server',
      healthcheck: { command: 'redis-cli ping', intervalSeconds: 5 },
    })
    expect(block).not.toContain('healthcheck')
    expect(block).not.toContain('redis-cli ping')
  })

  it('does NOT render the per-service install into the supervisord conf', () => {
    // Install is consumed by the install stage, not supervisord.
    const block = renderServiceBlock({
      name: 'mongo',
      command: 'mongod',
      install: 'apt-get install -y mongodb-org',
    })
    expect(block).not.toContain('apt-get')
    expect(block).not.toContain('install')
  })

  it('renders a realistic mongodb spec end-to-end (snapshot eyeball)', () => {
    // The spec the card explicitly calls out as a sanity check.
    const block = renderServiceBlock({
      name: 'mongodb',
      command: '/usr/bin/mongod --dbpath /data/x',
      user: 'mongodb',
      env: { MONGO_INITDB_ROOT: 'admin' },
    })
    expect(block).toBe(
      [
        '[program:mongodb]',
        'command=/usr/bin/mongod --dbpath /data/x',
        'user=mongodb',
        'autorestart=true',
        'stdout_logfile=/var/log/supervisor/mongodb.log',
        'stdout_logfile_maxbytes=10MB',
        'stdout_logfile_backups=3',
        'redirect_stderr=true',
        'environment=MONGO_INITDB_ROOT=admin',
      ].join('\n') + '\n',
    )
  })
})

// ============================================================================
// SupervisordController.addService — integration with fs + executor
// ============================================================================

describe('SupervisordController.addService', () => {
  it('writes conf and runs supervisorctl reread+update for a fresh service', async () => {
    const { executor, calls } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService(REDIS_SPEC)

    expect(fs.writeFile).toHaveBeenCalledTimes(1)
    const written = fs.files.get(`${CONF_DIR}/redis.conf`)
    expect(written).toBeDefined()
    expect(written).toContain('[program:redis]')
    expect(written).toContain('command=/usr/bin/redis-server --port 6379')

    expect(calls.map((c) => `${c.command} ${c.args.join(' ')}`)).toEqual([
      'supervisorctl reread',
      'supervisorctl update',
    ])
  })

  it('skips write + exec when the existing conf is byte-identical', async () => {
    const desired = renderServiceBlock(REDIS_SPEC)
    const { executor, run } = makeExecutor()
    const fs = makeFs({ [`${CONF_DIR}/redis.conf`]: desired })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService(REDIS_SPEC)

    expect(fs.writeFile).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })

  it('overwrites and reread+update when the existing conf differs', async () => {
    // Drift recovery: an old conf is on disk but doesn't match what the spec
    // would render today. Overwrite so supervisord picks up the new config.
    const stale = '[program:redis]\ncommand=/old/path\n'
    const { executor, calls } = makeExecutor()
    const fs = makeFs({ [`${CONF_DIR}/redis.conf`]: stale })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService(REDIS_SPEC)

    expect(fs.writeFile).toHaveBeenCalledTimes(1)
    const written = fs.files.get(`${CONF_DIR}/redis.conf`)
    expect(written).not.toBe(stale)
    expect(written).toContain('command=/usr/bin/redis-server')
    expect(calls.map((c) => `${c.command} ${c.args.join(' ')}`)).toEqual([
      'supervisorctl reread',
      'supervisorctl update',
    ])
  })

  it('renders postgres with the configured user', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService(POSTGRES_SPEC)

    const written = fs.files.get(`${CONF_DIR}/postgres.conf`) ?? ''
    expect(written).toContain('[program:postgres]')
    expect(written).toContain('user=postgres')
    expect(written).toContain('autorestart=true')
  })

  it('renders the environment line when env is provided', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService({
      name: 'minio',
      command: '/usr/local/bin/minio server /data/minio --address :9000',
      env: {
        MINIO_ROOT_USER: 'minioadmin',
        MINIO_ROOT_PASSWORD: 'minioadmin',
      },
    })

    const written = fs.files.get(`${CONF_DIR}/minio.conf`) ?? ''
    expect(written).toContain('environment=MINIO_ROOT_USER=minioadmin')
    expect(written).toContain('MINIO_ROOT_PASSWORD=minioadmin')
  })

  it('renders defaults (user=agent) when ServiceSpec omits user', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.addService({ name: 'mailhog', command: '/usr/local/bin/mailhog' })

    const written = fs.files.get(`${CONF_DIR}/mailhog.conf`) ?? ''
    expect(written).toContain('[program:mailhog]')
    expect(written).toContain('user=agent')
    expect(written).not.toContain('environment=')
  })

  it('pre-aborted signal short-circuits without writes', async () => {
    const { executor, run } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const ac = new AbortController()
    ac.abort()

    await expect(controller.addService(REDIS_SPEC, ac.signal)).rejects.toThrow(/aborted/i)
    expect(fs.writeFile).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })
})

// ============================================================================
// removeService — tears down a single service
// ============================================================================

describe('SupervisordController.removeService', () => {
  it('stops, removes, unlinks the conf, then reread+update', async () => {
    const { executor, calls } = makeExecutor()
    const fs = makeFs({
      [`${CONF_DIR}/postgres.conf`]: '[program:postgres]\ncommand=...\n',
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const wasPresent = await controller.removeService('postgres')

    expect(wasPresent).toBe(true)
    expect(fs.files.has(`${CONF_DIR}/postgres.conf`)).toBe(false)
    // The exact commands issued, in order: stop, remove, reread, update.
    const cmd = calls.map((c) => [c.command, ...c.args].join(' '))
    expect(cmd).toEqual([
      'supervisorctl stop postgres',
      'supervisorctl remove postgres',
      'supervisorctl reread',
      'supervisorctl update',
    ])
    // stop + remove must use allowNonZero so "no such process" doesn't throw.
    expect(calls[0]?.opts?.allowNonZero).toBe(true)
    expect(calls[1]?.opts?.allowNonZero).toBe(true)
  })

  it('returns false (and still runs the supervisorctl dance) when conf was already absent', async () => {
    // Idempotency contract: caller can safely invoke removeService for a
    // name that was never registered — the reread/update is cheap, the
    // unlink is tolerant of ENOENT.
    const { executor, run } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const wasPresent = await controller.removeService('never-existed')

    expect(wasPresent).toBe(false)
    // Even though the conf was absent, we still issue the supervisorctl
    // commands — supervisord may still have an in-memory record from a
    // previous spec apply that lost its conf via manual cleanup.
    expect(run).toHaveBeenCalled()
  })

  it('tolerates supervisorctl errors per step', async () => {
    // Each step is wrapped in its own try/catch — a thrown error from
    // supervisorctl (e.g. socket missing) must not abort the unlink or
    // the subsequent reread/update.
    const failingExec: IExecutor = {
      run: vi.fn(async () => {
        throw new Error('supervisorctl: cannot connect to /tmp/supervisor.sock')
      }),
    }
    const fs = makeFs({
      [`${CONF_DIR}/redis.conf`]: '[program:redis]\ncommand=...\n',
    })
    const controller = makeController({
      executor: failingExec,
      fs: fs.fs,
      confDir: CONF_DIR,
    })

    // Even though every supervisorctl call throws, the conf file is still
    // unlinked and the call resolves (does NOT reject).
    await expect(controller.removeService('redis')).resolves.toBe(true)
    expect(fs.files.has(`${CONF_DIR}/redis.conf`)).toBe(false)
  })
})

// ============================================================================
// listConfiguredServiceNames + reconcileServices
// ============================================================================

describe('SupervisordController.listConfiguredServiceNames', () => {
  it('returns names of .conf files (without extension)', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs({
      [`${CONF_DIR}/postgres.conf`]: 'x',
      [`${CONF_DIR}/redis.conf`]: 'x',
      [`${CONF_DIR}/agent.conf`]: 'x',
      // Non-.conf files must be skipped (operator-dropped junk, README, etc.)
      [`${CONF_DIR}/README.md`]: 'docs',
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const names = await controller.listConfiguredServiceNames()

    expect(names.sort()).toEqual(['agent', 'postgres', 'redis'])
  })

  it('returns empty array when the conf dir does not exist (fresh volume)', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs()
    // Override readdir to simulate ENOENT for the conf dir.
    ;(fs.readdir as ReturnType<typeof vi.fn>).mockImplementation(async () => {
      const err = Object.assign(new Error('ENOENT'), { code: 'ENOENT' })
      throw err
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const names = await controller.listConfiguredServiceNames()
    expect(names).toEqual([])
  })
})

describe('SupervisordController.reconcileServices', () => {
  it('removes orphan confs (on disk but not in desired set)', async () => {
    // Persistent volume carries `postgres.conf` and `dotnet-api.conf` from a
    // previous spec revision; the new spec only declares `redis`. Reconcile
    // should tear down the two orphans (preserving redis if it were
    // present).
    const { executor } = makeExecutor()
    const fs = makeFs({
      [`${CONF_DIR}/postgres.conf`]: '[program:postgres]\ncommand=...\n',
      [`${CONF_DIR}/dotnet-api.conf`]: '[program:dotnet-api]\ncommand=...\n',
      [`${CONF_DIR}/redis.conf`]: '[program:redis]\ncommand=...\n',
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const removed = await controller.reconcileServices(new Set(['redis']))

    expect(removed.sort()).toEqual(['dotnet-api', 'postgres'])
    expect(fs.files.has(`${CONF_DIR}/postgres.conf`)).toBe(false)
    expect(fs.files.has(`${CONF_DIR}/dotnet-api.conf`)).toBe(false)
    // Active service stays on disk.
    expect(fs.files.has(`${CONF_DIR}/redis.conf`)).toBe(true)
  })

  it('is a no-op when nothing needs reconciling', async () => {
    const { executor, run } = makeExecutor()
    const fs = makeFs({
      [`${CONF_DIR}/postgres.conf`]: '[program:postgres]\n',
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const removed = await controller.reconcileServices(new Set(['postgres']))

    expect(removed).toEqual([])
    expect(fs.files.has(`${CONF_DIR}/postgres.conf`)).toBe(true)
    // No supervisorctl shell-outs at all when nothing is orphan.
    expect(run).not.toHaveBeenCalled()
  })

  it('removes everything when desired set is empty (spec has zero services)', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs({
      [`${CONF_DIR}/postgres.conf`]: 'x',
      [`${CONF_DIR}/redis.conf`]: 'x',
    })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    const removed = await controller.reconcileServices(new Set())

    expect(removed.sort()).toEqual(['postgres', 'redis'])
    expect(fs.files.has(`${CONF_DIR}/postgres.conf`)).toBe(false)
    expect(fs.files.has(`${CONF_DIR}/redis.conf`)).toBe(false)
  })
})
