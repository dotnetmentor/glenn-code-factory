// Tests for CustomTools — the two platform-defined tools the daemon registers
// with Claude every turn.
//
// Pattern matches the rest of the daemon: hand-rolled SignalRClient stub,
// pino-shaped logger stub, no `vi.mock` of any module. The `exec` injection
// point on `buildRestartService` lets us drive supervisorctl-failure paths
// without touching real `node:child_process`.

import { execFile } from 'node:child_process'
import { promisify } from 'node:util'

import { describe, expect, it, vi } from 'vitest'
import Ajv from 'ajv'
import type { Logger } from 'pino'

import { DaemonConfig } from '../config/DaemonConfig.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { CustomTool, ToolContext } from '../turn/types.js'

import { buildCustomTools } from './CustomTools.js'
import type { ToolDescriptionResponse } from './fetchToolDescription.js'

// Stub propose_runtime_spec description + JSON schema. Real values come from
// the backend at startup (see `fetchToolDescription.ts`); the daemon-side tests
// only care that the strings flow through the factory verbatim.
const STUB_PROPOSE_RUNTIME_SPEC: ToolDescriptionResponse = {
  description: 'stub propose_runtime_spec description',
  inputSchema: {
    type: 'object',
    properties: {
      proposedSpec: { type: 'object' },
      reason: { type: 'string' },
    },
    required: ['proposedSpec', 'reason'],
  },
}

type ExecFileAsync = typeof execFileAsyncReal
const execFileAsyncReal = promisify(execFile)

/**
 * Cast a vi.fn-shaped exec stub to the typed `execFileAsync` shape the factory
 * expects. The runtime call signature we care about (`(file, args)`) is a
 * subset of the `PromiseWithChild`-returning overload set; the cast is purely
 * to silence the union-overload requirement in tests.
 */
function asExec(
  fn: (file: string, args: readonly string[]) => Promise<{ stdout: string; stderr: string }>,
): ExecFileAsync {
  return fn as unknown as ExecFileAsync
}

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

function makeConfig(): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
  })
}

interface LoggerStub {
  trace: ReturnType<typeof vi.fn>
  debug: ReturnType<typeof vi.fn>
  info: ReturnType<typeof vi.fn>
  warn: ReturnType<typeof vi.fn>
  error: ReturnType<typeof vi.fn>
  fatal: ReturnType<typeof vi.fn>
  child: ReturnType<typeof vi.fn>
  asLogger: Logger
}

function makeLoggerStub(): LoggerStub {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(),
  }
  log.child.mockImplementation(() => log)
  const stub = log as unknown as LoggerStub
  stub.asLogger = log as unknown as Logger
  return stub
}

function makeLogger(): Logger {
  return makeLoggerStub().asLogger
}

interface SignalrStub {
  stub: SignalRClient
}

// Minimal SignalR stub. The propose_runtime_spec tool no longer routes through
// SignalR (it does an HTTP POST direct to main API per Spec 16 Card 5), but
// restart_service still receives a SignalRClient via ToolContext, so we hand
// over an empty cast to satisfy the type. propose tests live in a sibling
// file (CustomTools.propose.test.ts) with their own fetch fake.
function makeSignalrStub(): SignalrStub {
  const stub = {} as unknown as SignalRClient
  return { stub }
}

function makeCtx(deps: { signalr: SignalRClient; config: DaemonConfig }): ToolContext {
  return {
    signalr: deps.signalr,
    config: deps.config,
    sessionId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    turnId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  }
}

function findTool(tools: readonly CustomTool[], name: string): CustomTool {
  const t = tools.find((x) => x.name === name)
  if (!t) throw new Error(`tool ${name} not found in factory output`)
  return t
}

// ============================================================================
// propose_runtime_spec — see CustomTools.propose.test.ts (full HTTP coverage).
// We keep one schema sanity check here so the inputSchema export stays exercised
// by the cross-tool invariants below if the propose file ever drifts.
// ============================================================================

describe('propose_runtime_spec (schema sanity)', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'propose_runtime_spec')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })
})

// ============================================================================
// restart_service
// ============================================================================

describe('restart_service', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'restart_service')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })

  it('rejects path-traversal-style names', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn(async () => ({ stdout: '', stderr: '' }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), exec: asExec(exec) })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: '../etc/passwd', reason: 'definitely a real reason' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(exec).not.toHaveBeenCalled()
  })

  it('rejects names with shell metacharacters', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn(async () => ({ stdout: '', stderr: '' }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), exec: asExec(exec) })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: 'foo;rm -rf /', reason: 'definitely a real reason' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(exec).not.toHaveBeenCalled()
  })

  it('rejects when reason is missing', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn(async () => ({ stdout: '', stderr: '' }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), exec: asExec(exec) })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: 'webapp' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/reason/)
    expect(exec).not.toHaveBeenCalled()
  })

  it('returns not_implemented when no approval hook is wired (current state)', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn(async () => ({ stdout: '', stderr: '' }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), exec: asExec(exec) })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: 'webapp', reason: 'process is wedged on a stuck connection' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; approved?: boolean; reason?: string }
    expect(result.ok).toBe(false)
    expect(result.approved).toBe(false)
    expect(result.reason).toBe('not_implemented')
    expect(exec).not.toHaveBeenCalled()
  })

  it('speculative-future: when approveRestart returns approved, exec is called', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn<(file: string, args: readonly string[]) => Promise<{
      stdout: string
      stderr: string
    }>>(async () => ({
      stdout: 'webapp: stopped\nwebapp: started',
      stderr: '',
    }))
    const approveRestart = vi.fn(async () => ({ approved: true as const }))
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      exec: asExec(exec),
      approveRestart,
    })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: 'webapp', reason: 'process is wedged on a stuck connection' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; stdout?: string }

    expect(result.ok).toBe(true)
    expect(result.stdout).toMatch(/webapp: started/)
    expect(exec).toHaveBeenCalledTimes(1)
    const call = exec.mock.calls[0]
    expect(call).toBeDefined()
    expect(call![0]).toBe('supervisorctl')
    expect(call![1]).toEqual(['restart', 'webapp'])
  })

  it('returns supervisorctl_failed when exec rejects', async () => {
    const logger = makeLoggerStub()
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const exec = vi.fn(async () => {
      throw new Error('spawn supervisorctl ENOENT')
    })
    const approveRestart = vi.fn(async () => ({ approved: true as const }))
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: logger.asLogger,
      exec: asExec(exec),
      approveRestart,
    })
    const tool = findTool(tools, 'restart_service')

    const result = (await tool.run(
      { name: 'webapp', reason: 'process is wedged on a stuck connection' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string; message?: string }

    expect(result.ok).toBe(false)
    expect(result.error).toBe('supervisorctl_failed')
    expect(result.message).toMatch(/ENOENT/)
    expect(logger.error).toHaveBeenCalled()
  })
})

// ============================================================================
// dry_run_install
// ============================================================================

import type { ExecOpts, ExecResult, IExecutor } from '../runtime/IExecutor.js'

interface ExecutorCall {
  command: string
  args: readonly string[]
  opts: ExecOpts
}

/**
 * Fake IExecutor that records every call and returns whatever the test handler
 * produces. Streaming callbacks (`onStdout` / `onStderr`) are invoked
 * synchronously with the captured stdout/stderr before resolving, mirroring
 * what `ChildProcessExecutor`'s spawn path does on a real run.
 */
function makeFakeExecutor(
  handler: (call: ExecutorCall) => ExecResult | Promise<ExecResult> | Error,
): {
  executor: IExecutor
  calls: ExecutorCall[]
} {
  const calls: ExecutorCall[] = []
  const executor: IExecutor = {
    async run(command, args, opts = {}) {
      const call: ExecutorCall = { command, args, opts }
      calls.push(call)
      const result = handler(call)
      const resolved = result instanceof Promise ? await result : result
      if (resolved instanceof Error) throw resolved
      // Mirror ChildProcessExecutor: stream the captured chunks before
      // returning, so callers that wire onStdout/onStderr see the data.
      if (opts.onStdout !== undefined && resolved.stdout.length > 0) {
        opts.onStdout(resolved.stdout)
      }
      if (opts.onStderr !== undefined && resolved.stderr.length > 0) {
        opts.onStderr(resolved.stderr)
      }
      return resolved
    },
  }
  return { executor, calls }
}

describe('dry_run_install', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const { executor } = makeFakeExecutor(() => ({
      stdout: '',
      stderr: '',
      exitCode: 0,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })

  it('rejects when script is missing', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const { executor, calls } = makeFakeExecutor(() => ({
      stdout: '',
      stderr: '',
      exitCode: 0,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      error?: string
    }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(calls).toHaveLength(0)
  })

  it('runs bash -c with the bootstrap PATH and cwd "/"', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const { executor, calls } = makeFakeExecutor(() => ({
      stdout: 'hello\n',
      stderr: '',
      exitCode: 0,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    const result = (await tool.run(
      { script: 'echo hello' },
      makeCtx({ signalr, config }),
    )) as {
      ok: boolean
      exitCode: number
      stdoutTail: string
      stderrTail: string
      timedOut: boolean
    }

    expect(result.ok).toBe(true)
    expect(result.exitCode).toBe(0)
    expect(result.stdoutTail).toBe('hello\n')
    expect(result.stderrTail).toBe('')
    expect(result.timedOut).toBe(false)

    expect(calls).toHaveLength(1)
    const call = calls[0]!
    expect(call.command).toBe('bash')
    expect(call.args[0]).toBe('-c')
    // The script body must appear inside a heredoc so multi-line scripts work
    // identically to InstallStage.
    expect(call.args[1]).toContain('__GLENN_DRY_RUN_EOF__')
    expect(call.args[1]).toContain('echo hello')
    // cwd + env match the bootstrap install stage exactly.
    expect(call.opts.cwd).toBe('/')
    expect(call.opts.env?.['PATH']).toContain('/data/mise/shims')
    expect(call.opts.env?.['PATH']).toContain('/usr/local/bin')
    expect(call.opts.env?.['PATH']).not.toContain('/data/.mise')
    // allowNonZero must be true so a failing snippet returns the exit code
    // instead of throwing.
    expect(call.opts.allowNonZero).toBe(true)
  })

  it('surfaces non-zero exit code without throwing', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const { executor } = makeFakeExecutor(() => ({
      stdout: '',
      stderr: '/tmp/x.sh: line 1: nope: command not found\n',
      exitCode: 127,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    const result = (await tool.run(
      { script: 'nope' },
      makeCtx({ signalr, config }),
    )) as {
      ok: boolean
      exitCode: number
      stderrTail: string
      timedOut: boolean
    }

    expect(result.ok).toBe(true)
    expect(result.exitCode).toBe(127)
    expect(result.stderrTail).toMatch(/command not found/)
    expect(result.timedOut).toBe(false)
  })

  it('marks timedOut: true when the executor reports a signal kill', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const { executor } = makeFakeExecutor(
      () => new Error('bash killed by signal SIGKILL (timeoutMs=1000)'),
    )
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    const result = (await tool.run(
      { script: 'sleep 999', timeoutMs: 1000 },
      makeCtx({ signalr, config }),
    )) as {
      ok: boolean
      timedOut: boolean
      exitCode: number
    }

    expect(result.ok).toBe(true)
    expect(result.timedOut).toBe(true)
    expect(result.exitCode).toBe(-1)
  })

  it('tail-trims stdout to the last 8 KB (failure tail is what matters)', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    // 9 KB of distinguishable output — last line is the one we care about.
    const filler = 'x'.repeat(9 * 1024)
    const fullStdout = `${filler}TAIL_MARKER\n`
    const { executor } = makeFakeExecutor(() => ({
      stdout: fullStdout,
      stderr: '',
      exitCode: 0,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    const result = (await tool.run(
      { script: 'echo big' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; stdoutTail: string }

    expect(result.ok).toBe(true)
    expect(Buffer.byteLength(result.stdoutTail, 'utf8')).toBeLessThanOrEqual(
      8 * 1024,
    )
    expect(result.stdoutTail).toContain('TAIL_MARKER')
  })

  it('cleans up the /tmp script via `rm -f` regardless of exit code', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const { executor, calls } = makeFakeExecutor(() => ({
      stdout: '',
      stderr: '',
      exitCode: 1,
    }))
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger(), executor })
    const tool = findTool(tools, 'dry_run_install')

    await tool.run(
      { script: 'exit 1' },
      makeCtx({ signalr, config }),
    )

    expect(calls).toHaveLength(1)
    expect(calls[0]!.args[1]).toMatch(/rm -f "\/tmp\/dry-run-install-/)
  })
})

// ============================================================================
// get_runtime_spec  (self-healing-runtime-specs, card D2)
// ============================================================================

import type { BootIssue } from '../bootstrap/BootIssueStore.js'

describe('get_runtime_spec', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'get_runtime_spec')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })

  it('returns the current applied spec + version from the bound snapshot', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const spec = { version: 2, install: 'echo hi', services: [{ name: 'web', command: 'node x' }] }
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      getRuntimeSpec: () => ({ version: 'v7', runtimeSpec: spec }),
    })
    const tool = findTool(tools, 'get_runtime_spec')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      available: boolean
      version?: string
      spec?: unknown
    }
    expect(result.ok).toBe(true)
    expect(result.available).toBe(true)
    expect(result.version).toBe('v7')
    expect(result.spec).toEqual(spec)
  })

  it('reports available:false when no spec snapshot is available yet', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      getRuntimeSpec: () => null,
    })
    const tool = findTool(tools, 'get_runtime_spec')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      available: boolean
      version?: string
    }
    expect(result.ok).toBe(true)
    expect(result.available).toBe(false)
    expect(result.version).toBeUndefined()
  })

  it('degrades to available:false when the snapshot getter throws', async () => {
    const logger = makeLoggerStub()
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: logger.asLogger,
      getRuntimeSpec: () => {
        throw new Error('BootstrapState.payload read before FetchingStage populated it')
      },
    })
    const tool = findTool(tools, 'get_runtime_spec')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      available: boolean
    }
    expect(result.ok).toBe(true)
    expect(result.available).toBe(false)
    expect(logger.warn).toHaveBeenCalled()
  })

  it('reports available:false when no getRuntimeSpec dep is wired', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'get_runtime_spec')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      available: boolean
    }
    expect(result.ok).toBe(true)
    expect(result.available).toBe(false)
  })
})

// ============================================================================
// get_boot_issues  (self-healing-runtime-specs, card D2)
// ============================================================================

describe('get_boot_issues', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'get_boot_issues')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })

  it('returns the bound boot-issue list with count', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const issues: BootIssue[] = [
      {
        stage: 'install',
        reason: 'install bash exited 127',
        detail: 'foo: command not found',
        occurredAt: '2026-05-31T00:00:00.000Z',
      },
      {
        stage: 'starting-services',
        service: 'web',
        reason: 'service never bound to its port',
        occurredAt: '2026-05-31T00:00:01.000Z',
      },
    ]
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      listBootIssues: () => issues,
    })
    const tool = findTool(tools, 'get_boot_issues')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      count: number
      issues: BootIssue[]
    }
    expect(result.ok).toBe(true)
    expect(result.count).toBe(2)
    expect(result.issues).toEqual(issues)
    expect(result.issues[1]!.service).toBe('web')
  })

  it('returns count:0 and an empty list for a clean boot', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      listBootIssues: () => [],
    })
    const tool = findTool(tools, 'get_boot_issues')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      count: number
      issues: BootIssue[]
    }
    expect(result.ok).toBe(true)
    expect(result.count).toBe(0)
    expect(result.issues).toEqual([])
  })

  it('degrades to an empty list when the lister throws', async () => {
    const logger = makeLoggerStub()
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: logger.asLogger,
      listBootIssues: () => {
        throw new Error('boom')
      },
    })
    const tool = findTool(tools, 'get_boot_issues')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      count: number
    }
    expect(result.ok).toBe(true)
    expect(result.count).toBe(0)
    expect(logger.warn).toHaveBeenCalled()
  })
})

// ============================================================================
// request_rebootstrap  (self-healing-runtime-specs, card D2 — escape hatch)
// ============================================================================

describe('request_rebootstrap', () => {
  it('JSON Schema compiles via Ajv', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'request_rebootstrap')
    const ajv = new Ajv()
    expect(() => ajv.compile(tool.inputSchema)).not.toThrow()
  })

  it('rejects when reason is missing', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const triggerRebootstrap = vi.fn(async () => {})
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      triggerRebootstrap,
    })
    const tool = findTool(tools, 'request_rebootstrap')

    const result = (await tool.run({}, makeCtx({ signalr, config }))) as {
      ok: boolean
      error?: string
    }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(triggerRebootstrap).not.toHaveBeenCalled()
  })

  it('rejects a too-short reason (under 10 chars)', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const triggerRebootstrap = vi.fn(async () => {})
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      triggerRebootstrap,
    })
    const tool = findTool(tools, 'request_rebootstrap')

    const result = (await tool.run(
      { reason: 'too short' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(triggerRebootstrap).not.toHaveBeenCalled()
  })

  it('rejects a whitespace-only reason that passes raw length', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const triggerRebootstrap = vi.fn(async () => {})
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      triggerRebootstrap,
    })
    const tool = findTool(tools, 'request_rebootstrap')

    const result = (await tool.run(
      { reason: '              ' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; error?: string }
    expect(result.ok).toBe(false)
    expect(result.error).toMatch(/invalid/i)
    expect(triggerRebootstrap).not.toHaveBeenCalled()
  })

  it('fires the bound rebootstrap trigger on a valid reason', async () => {
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    let resolveTrigger: () => void = () => {}
    const triggered = new Promise<void>((r) => {
      resolveTrigger = r
    })
    const triggerRebootstrap = vi.fn(async (_reason: string) => {
      resolveTrigger()
    })
    const tools = buildCustomTools({
      proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC,
      config,
      logger: makeLogger(),
      triggerRebootstrap,
    })
    const tool = findTool(tools, 'request_rebootstrap')

    const result = (await tool.run(
      { reason: 'rootfs is wedged after the fixed spec applied; need a clean reboot' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean; message?: string }

    expect(result.ok).toBe(true)
    expect(result.message).toMatch(/restarting/i)
    // The trigger fires asynchronously (fire-and-forget); await its signal.
    await triggered
    expect(triggerRebootstrap).toHaveBeenCalledTimes(1)
    expect(triggerRebootstrap.mock.calls[0]![0]).toMatch(/rootfs is wedged/)
  })

  it('acknowledges (ok:true) even when no trigger dep is wired', async () => {
    const logger = makeLoggerStub()
    const { stub: signalr } = makeSignalrStub()
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: logger.asLogger })
    const tool = findTool(tools, 'request_rebootstrap')

    const result = (await tool.run(
      { reason: 'a perfectly valid ten plus character reason here' },
      makeCtx({ signalr, config }),
    )) as { ok: boolean }
    expect(result.ok).toBe(true)
  })
})

// ============================================================================
// Cross-tool invariants
// ============================================================================

describe('buildCustomTools', () => {
  it('returns array of length 6 with the six known names', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    expect(tools).toHaveLength(6)
    const names = tools.map((t) => t.name).sort()
    expect(names).toEqual([
      'dry_run_install',
      'get_boot_issues',
      'get_runtime_spec',
      'propose_runtime_spec',
      'request_rebootstrap',
      'restart_service',
    ])
  })

  it("every tool's inputSchema compiles cleanly via Ajv", () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const ajv = new Ajv()
    for (const t of tools) {
      expect(() => ajv.compile(t.inputSchema)).not.toThrow()
    }
  })

  it('the three self-heal tools document the primary loop in their descriptions', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    for (const name of ['get_runtime_spec', 'get_boot_issues', 'request_rebootstrap']) {
      const tool = findTool(tools, name)
      expect(tool.description).toContain('get_boot_issues')
      expect(tool.description).toContain('get_runtime_spec')
      expect(tool.description).toContain('dry_run_install')
      expect(tool.description).toContain('propose_runtime_spec')
    }
  })

  it('request_rebootstrap warns it restarts the runtime and ends the turn', () => {
    const config = makeConfig()
    const tools = buildCustomTools({ proposeRuntimeSpec: STUB_PROPOSE_RUNTIME_SPEC, config, logger: makeLogger() })
    const tool = findTool(tools, 'request_rebootstrap')
    expect(tool.description).toMatch(/RESTARTS THE RUNTIME/i)
    expect(tool.description).toMatch(/ENDS THE CURRENT AGENT TURN/i)
  })
})
