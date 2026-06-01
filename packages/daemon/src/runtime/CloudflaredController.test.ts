// Tests for CloudflaredController. Hand-rolled fake fs + executor — no real
// disk IO, no real supervisorctl. Exercises both the conf-renderer
// (CloudflaredController.render) and the controller's idempotency / write /
// reread behaviour.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { ExecOpts, ExecResult, IExecutor } from './IExecutor.js'
import {
  CloudflaredController,
  type CloudflaredConfig,
  type CloudflaredControllerFs,
} from './CloudflaredController.js'

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

function makeFs(initialFiles: Record<string, string> = {}): {
  fs: CloudflaredControllerFs
  files: Map<string, string>
  writeFile: ReturnType<typeof vi.fn>
  access: ReturnType<typeof vi.fn>
  readFile: ReturnType<typeof vi.fn>
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
  return {
    fs: {
      writeFile: writeFile as unknown as CloudflaredControllerFs['writeFile'],
      access: access as unknown as CloudflaredControllerFs['access'],
      readFile: readFile as unknown as CloudflaredControllerFs['readFile'],
    },
    files,
    writeFile,
    access,
    readFile,
  }
}

const CONF_DIR = '/data/.glenn/supervisor.d'
const CONF_PATH = `${CONF_DIR}/cloudflared.conf`

function makeController(opts: {
  executor: IExecutor
  fs: CloudflaredControllerFs
  confDir?: string
}) {
  return new CloudflaredController({
    executor: opts.executor,
    fs: opts.fs,
    logger: makeLogger() as unknown as Logger,
    ...(opts.confDir !== undefined ? { confDir: opts.confDir } : {}),
  })
}

const BASE_CONFIG: CloudflaredConfig = {
  tunnelToken: 'tok-abc-123',
  previewPort: 5173,
  previewHostname: 'preview-foo.example.dev',
}

// ============================================================================
// CloudflaredController.render — pure function tests
// ============================================================================

describe('CloudflaredController.render', () => {
  it('produces stable output for a given config', () => {
    const rendered = CloudflaredController.render(BASE_CONFIG)
    expect(rendered).toBe(
      [
        '[program:cloudflared]',
        'command=/usr/local/bin/cloudflared tunnel run --token tok-abc-123',
        'directory=/data',
        'autostart=true',
        'autorestart=true',
        'startsecs=5',
        'startretries=3',
        'stopwaitsecs=10',
        'stopsignal=TERM',
        'stdout_logfile=/var/log/supervisor/cloudflared.out.log',
        'stderr_logfile=/var/log/supervisor/cloudflared.err.log',
        'stdout_logfile_maxbytes=10MB',
        'stderr_logfile_maxbytes=10MB',
      ].join('\n') + '\n',
    )
  })

  it('embeds the token literally into the command line', () => {
    const rendered = CloudflaredController.render({
      ...BASE_CONFIG,
      tunnelToken: 'eyJhbGciOiJIUzI1NiJ9.payload.sig',
    })
    expect(rendered).toContain(
      'command=/usr/local/bin/cloudflared tunnel run --token eyJhbGciOiJIUzI1NiJ9.payload.sig',
    )
  })

  it('changes output when the token changes (so apply() can detect drift)', () => {
    const a = CloudflaredController.render({ ...BASE_CONFIG, tunnelToken: 'tok-a' })
    const b = CloudflaredController.render({ ...BASE_CONFIG, tunnelToken: 'tok-b' })
    expect(a).not.toBe(b)
  })

  it('does not depend on previewHostname (logs-only field)', () => {
    const withHost = CloudflaredController.render(BASE_CONFIG)
    const withoutHost = CloudflaredController.render({
      tunnelToken: BASE_CONFIG.tunnelToken,
      previewPort: BASE_CONFIG.previewPort,
    })
    expect(withHost).toBe(withoutHost)
  })
})

// ============================================================================
// CloudflaredController.apply — integration with fs + executor
// ============================================================================

describe('CloudflaredController.apply', () => {
  it('writes conf and runs supervisorctl reread+update on first call', async () => {
    const { executor, calls } = makeExecutor()
    const fs = makeFs()
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.apply(BASE_CONFIG)

    expect(fs.writeFile).toHaveBeenCalledTimes(1)
    const written = fs.files.get(CONF_PATH)
    expect(written).toBeDefined()
    expect(written).toContain('[program:cloudflared]')
    expect(written).toContain('--token tok-abc-123')

    expect(calls.map((c) => `${c.command} ${c.args.join(' ')}`)).toEqual([
      'supervisorctl reread',
      'supervisorctl update',
    ])
  })

  it('is a no-op when the existing conf is byte-identical', async () => {
    const desired = CloudflaredController.render(BASE_CONFIG)
    const { executor, run } = makeExecutor()
    const fs = makeFs({ [CONF_PATH]: desired })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.apply(BASE_CONFIG)

    expect(fs.writeFile).not.toHaveBeenCalled()
    expect(run).not.toHaveBeenCalled()
  })

  it('overwrites and reread+update when the token changes', async () => {
    // Token rotation / drift recovery path: an old conf is on disk with a
    // stale token. Re-applying with a new token should rewrite the conf and
    // notify supervisord.
    const stale = CloudflaredController.render({
      ...BASE_CONFIG,
      tunnelToken: 'old-token',
    })
    const { executor, calls } = makeExecutor()
    const fs = makeFs({ [CONF_PATH]: stale })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.apply({ ...BASE_CONFIG, tunnelToken: 'new-token' })

    expect(fs.writeFile).toHaveBeenCalledTimes(1)
    const written = fs.files.get(CONF_PATH) ?? ''
    expect(written).not.toBe(stale)
    expect(written).toContain('--token new-token')
    expect(written).not.toContain('--token old-token')
    expect(calls.map((c) => `${c.command} ${c.args.join(' ')}`)).toEqual([
      'supervisorctl reread',
      'supervisorctl update',
    ])
  })

  it('overwrites when the existing conf has arbitrary drifted content', async () => {
    const stale = '[program:cloudflared]\ncommand=/old/path\n'
    const { executor, calls } = makeExecutor()
    const fs = makeFs({ [CONF_PATH]: stale })
    const controller = makeController({ executor, fs: fs.fs, confDir: CONF_DIR })

    await controller.apply(BASE_CONFIG)

    expect(fs.writeFile).toHaveBeenCalledTimes(1)
    const written = fs.files.get(CONF_PATH) ?? ''
    expect(written).not.toBe(stale)
    expect(written).toContain('command=/usr/local/bin/cloudflared')
    expect(calls.map((c) => `${c.command} ${c.args.join(' ')}`)).toEqual([
      'supervisorctl reread',
      'supervisorctl update',
    ])
  })

  it('writes to the configured confDir', async () => {
    const { executor } = makeExecutor()
    const fs = makeFs()
    const customDir = '/tmp/custom/supervisor.d'
    const controller = makeController({ executor, fs: fs.fs, confDir: customDir })

    await controller.apply(BASE_CONFIG)

    expect(fs.files.has(`${customDir}/cloudflared.conf`)).toBe(true)
    expect(fs.files.has(CONF_PATH)).toBe(false)
  })
})
