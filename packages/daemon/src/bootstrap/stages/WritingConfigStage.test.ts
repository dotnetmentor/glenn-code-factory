// Tests for WritingConfigStage. Hand-rolled fakes for fs / signalr /
// envVarManager / mcpRegistry so nothing actually touches disk.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'
import type { BootstrapPayloadV2 } from '../../signalr/types.js'

import { BootstrapState } from '../BootstrapState.js'
import { WritingConfigStage, type WritingConfigStageFs } from './WritingConfigStage.js'

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

function makeContext(opts: { signal?: AbortSignal } = {}): BootstrapContext {
  return {
    config: {} as DaemonConfig,
    signalr: {} as SignalRClient,
    logger: makeLogger() as unknown as Logger,
    signal: opts.signal ?? new AbortController().signal,
  }
}

function validPayload(): BootstrapPayloadV2 {
  return {
    version: 'v2',
    runtimeSpec: { version: 2, setup: 'npm ci' },
    envVars: [
      { key: 'FOO', value: 'bar' },
      { key: 'API_KEY', value: 'sk-secret' },
    ],
    hooks: { 'pre-tool': 'echo hi' } as unknown as BootstrapPayloadV2['hooks'],
    mcps: [
      { name: 'github', url: 'http://api/github', scope: 'opaque' },
      { name: 'jira', url: 'http://api/jira', scope: 'opaque' },
    ],
    repo: null,
  }
}

interface FsCall {
  op: 'mkdir' | 'writeFile' | 'rename'
  path: string
  contents?: string
}

function makeFs(opts: { failOn?: 'mkdir' | 'writeFile' | 'rename' } = {}): {
  fs: WritingConfigStageFs
  calls: FsCall[]
} {
  const calls: FsCall[] = []
  const fs: WritingConfigStageFs = {
    mkdir: vi.fn(async (path: unknown) => {
      calls.push({ op: 'mkdir', path: String(path) })
      if (opts.failOn === 'mkdir') throw new Error('ENOSPC mkdir')
      return undefined
    }) as unknown as WritingConfigStageFs['mkdir'],
    writeFile: vi.fn(async (path: unknown, contents: unknown) => {
      calls.push({ op: 'writeFile', path: String(path), contents: String(contents) })
      if (opts.failOn === 'writeFile') throw new Error('EACCES writeFile')
    }) as unknown as WritingConfigStageFs['writeFile'],
    rename: vi.fn(async (from: unknown, to: unknown) => {
      calls.push({ op: 'rename', path: `${String(from)}->${String(to)}` })
      if (opts.failOn === 'rename') throw new Error('EXDEV rename')
    }) as unknown as WritingConfigStageFs['rename'],
  }
  return { fs, calls }
}

describe('WritingConfigStage', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('happy path: writes hooks.json + mcp.json atomically and seeds env + mcp registry', async () => {
    const { fs, calls } = makeFs()
    const loadInitialEnv = vi.fn(async () => {})
    const loadInitialMcp = vi.fn()
    const reportBootstrapProgress = vi.fn(async () => {})
    const state = new BootstrapState()
    state.setPayload(validPayload())
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress },
      state,
      fs,
      envVarManager: { loadInitial: loadInitialEnv },
      mcpRegistry: { loadInitial: loadInitialMcp },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    expect(loadInitialEnv).toHaveBeenCalledWith([
      { key: 'FOO', value: 'bar' },
      { key: 'API_KEY', value: 'sk-secret' },
    ])
    expect(loadInitialMcp).toHaveBeenCalledTimes(1)
    const seededMcps = loadInitialMcp.mock.calls[0]?.[0]
    expect(seededMcps).toEqual([
      { name: 'github', version: '1.0.0', baseUrl: 'http://api/github' },
      { name: 'jira', version: '1.0.0', baseUrl: 'http://api/jira' },
    ])

    // mkdir was called for the base dir.
    expect(calls.some((c) => c.op === 'mkdir' && c.path === '/tmp/glenn')).toBe(true)

    // hooks.json + mcp.json each: writeFile to .tmp then rename.
    expect(calls).toContainEqual(
      expect.objectContaining({ op: 'rename', path: '/tmp/glenn/hooks.json.tmp->/tmp/glenn/hooks.json' }),
    )
    expect(calls).toContainEqual(
      expect.objectContaining({ op: 'rename', path: '/tmp/glenn/mcp.json.tmp->/tmp/glenn/mcp.json' }),
    )

    // Hooks file contains the JSON-serialised hooks payload.
    const hooksWrite = calls.find((c) => c.op === 'writeFile' && c.path === '/tmp/glenn/hooks.json.tmp')
    expect(hooksWrite?.contents).toContain('"pre-tool"')
    expect(hooksWrite?.contents).toContain('"echo hi"')

    // mcp file contains both servers.
    const mcpWrite = calls.find((c) => c.op === 'writeFile' && c.path === '/tmp/glenn/mcp.json.tmp')
    expect(mcpWrite?.contents).toContain('"github"')
    expect(mcpWrite?.contents).toContain('"jira"')
  })

  it('writes empty {} hooks when payload.hooks is null', async () => {
    const { fs, calls } = makeFs()
    const state = new BootstrapState()
    state.setPayload({ ...validPayload(), hooks: null })
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      fs,
      envVarManager: { loadInitial: vi.fn(async () => {}) },
      mcpRegistry: { loadInitial: vi.fn() },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext())
    expect(result).toEqual({ ok: true })

    const hooksWrite = calls.find((c) => c.op === 'writeFile' && c.path === '/tmp/glenn/hooks.json.tmp')
    expect(hooksWrite?.contents).toBe('{}\n')
  })

  it('disk write failure (rename) returns non-recoverable failure', async () => {
    const { fs } = makeFs({ failOn: 'rename' })
    const state = new BootstrapState()
    state.setPayload(validPayload())
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      fs,
      envVarManager: { loadInitial: vi.fn(async () => {}) },
      mcpRegistry: { loadInitial: vi.fn() },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/EXDEV rename/)
    }
  })

  it('mkdir failure returns non-recoverable failure', async () => {
    const { fs } = makeFs({ failOn: 'mkdir' })
    const state = new BootstrapState()
    state.setPayload(validPayload())
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      fs,
      envVarManager: { loadInitial: vi.fn(async () => {}) },
      mcpRegistry: { loadInitial: vi.fn() },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/ENOSPC/)
    }
  })

  it('envVarManager.loadInitial failure returns non-recoverable failure', async () => {
    const { fs } = makeFs()
    const loadInitialEnv = vi.fn(async () => {
      throw new Error('env disk write blew up')
    })
    const state = new BootstrapState()
    state.setPayload(validPayload())
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      fs,
      envVarManager: { loadInitial: loadInitialEnv },
      mcpRegistry: { loadInitial: vi.fn() },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext())
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(false)
      expect(result.reason).toMatch(/env disk write blew up/)
    }
  })

  it('pre-aborted signal returns recoverable failure without touching fs', async () => {
    const { fs, calls } = makeFs()
    const ac = new AbortController()
    ac.abort()
    const state = new BootstrapState()
    state.setPayload(validPayload())
    const stage = new WritingConfigStage({
      signalr: { reportBootstrapProgress: vi.fn(async () => {}) },
      state,
      fs,
      envVarManager: { loadInitial: vi.fn(async () => {}) },
      mcpRegistry: { loadInitial: vi.fn() },
      baseDir: '/tmp/glenn',
    })

    const result = await stage.run(makeContext({ signal: ac.signal }))
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(calls).toHaveLength(0)
  })
})
