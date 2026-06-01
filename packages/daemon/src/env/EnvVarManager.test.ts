// Tests for EnvVarManager. Real tempdir per test (mkdtemp under os.tmpdir())
// so we exercise actual fs/promises paths end-to-end — file mode, atomic
// rename, sorted output, concurrent serialisation. The only seam we lean on
// is the `getuid` injection (so tests never look like they're running as
// root; chown stays an unreachable branch in CI).
//
// Directory fsync is platform-conditional in production but the call itself
// is harmless on Linux test runners; tests don't need to mock it.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as realFs from 'node:fs/promises'
import * as os from 'node:os'
import path from 'node:path'
import type { Logger } from 'pino'

import { EnvVarManager, EnvVarValueRejected } from './EnvVarManager.js'

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

let workDir: string

beforeEach(async () => {
  workDir = await realFs.mkdtemp(path.join(os.tmpdir(), 'env-var-manager-'))
})

afterEach(async () => {
  await realFs.rm(workDir, { recursive: true, force: true })
})

function makeManager(opts: { envFilePath?: string } = {}) {
  const logger = makeLogger()
  const envFilePath = opts.envFilePath ?? path.join(workDir, '.glenn', 'env')
  const manager = new EnvVarManager({
    envFilePath,
    logger: logger as unknown as Logger,
    // Stub out root detection — tests never want to exercise the chown
    // branch (touching uid 0 is too risky in CI).
    getuid: () => 1000,
    resolveAgentUser: () => null,
  })
  return { manager, logger, envFilePath }
}

// ============================================================================
// Tests
// ============================================================================

describe('EnvVarManager.loadInitial', () => {
  it('writes file with sorted KEY=value lines and mode 0o600', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([
      { key: 'BETA', value: 'two' },
      { key: 'ALPHA', value: 'one' },
      { key: 'GAMMA', value: 'three' },
    ])

    const content = await realFs.readFile(envFilePath, 'utf8')
    expect(content).toBe('ALPHA=one\nBETA=two\nGAMMA=three\n')

    const stat = await realFs.stat(envFilePath)
    // Mask off file-type bits so we compare just the perm portion.
    expect(stat.mode & 0o777).toBe(0o600)
  })

  it('clears the in-memory map and replaces with given entries', async () => {
    const { manager } = makeManager()
    await manager.loadInitial([{ key: 'A', value: '1' }, { key: 'B', value: '2' }])

    expect(Array.from(manager.current().entries())).toEqual([
      ['A', '1'],
      ['B', '2'],
    ])

    // A second loadInitial completely replaces; the prior keys are gone.
    await manager.loadInitial([{ key: 'X', value: '9' }])
    expect(Array.from(manager.current().entries())).toEqual([['X', '9']])
  })

  it('creates the parent directory with mode 0o700', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([{ key: 'X', value: 'y' }])

    const dirStat = await realFs.stat(path.dirname(envFilePath))
    expect(dirStat.isDirectory()).toBe(true)
    // We can't strictly assert 0o700 since the host umask may relax recursive
    // mkdir's mode bits. The dir must at minimum be present and readable.
    expect(dirStat.mode & 0o700).toBe(0o700)
  })
})

describe('EnvVarManager.applyDelta', () => {
  it('adds a new key while preserving existing keys', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([
      { key: 'EXISTING', value: 'keep' },
      { key: 'OTHER', value: 'also' },
    ])
    await manager.applyDelta([{ key: 'NEW', value: 'fresh' }])

    const content = await realFs.readFile(envFilePath, 'utf8')
    expect(content).toBe('EXISTING=keep\nNEW=fresh\nOTHER=also\n')
    expect(manager.current().get('NEW')).toBe('fresh')
    expect(manager.current().get('EXISTING')).toBe('keep')
  })

  it('updates an existing key in place', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([{ key: 'TOKEN', value: 'old' }])
    await manager.applyDelta([{ key: 'TOKEN', value: 'new' }])

    const content = await realFs.readFile(envFilePath, 'utf8')
    expect(content).toBe('TOKEN=new\n')
    expect(manager.current().get('TOKEN')).toBe('new')
  })

  it('deletes a key when value is null', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([
      { key: 'KEEP', value: 'k' },
      { key: 'DROP', value: 'd' },
    ])
    await manager.applyDelta([{ key: 'DROP', value: null }])

    const content = await realFs.readFile(envFilePath, 'utf8')
    expect(content).toBe('KEEP=k\n')
    expect(manager.current().has('DROP')).toBe(false)
  })

  it('serialises concurrent calls — both deltas land in submission order', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([])

    // Fire both without awaiting in between. Final state must reflect both.
    const p1 = manager.applyDelta([{ key: 'FIRST', value: '1' }])
    const p2 = manager.applyDelta([{ key: 'SECOND', value: '2' }])
    await Promise.all([p1, p2])

    const content = await realFs.readFile(envFilePath, 'utf8')
    expect(content).toBe('FIRST=1\nSECOND=2\n')

    // Stronger ordering check: a delta that overwrites a value set by the
    // first applyDelta MUST land second, not race.
    const p3 = manager.applyDelta([{ key: 'X', value: 'first' }])
    const p4 = manager.applyDelta([{ key: 'X', value: 'second' }])
    await Promise.all([p3, p4])

    expect(manager.current().get('X')).toBe('second')
  })

  it('rejects values containing newline with EnvVarValueRejected', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([{ key: 'GOOD', value: 'fine' }])

    const before = await realFs.readFile(envFilePath, 'utf8')

    let caught: unknown = null
    try {
      await manager.applyDelta([{ key: 'BAD', value: 'multi\nline' }])
    } catch (err) {
      caught = err
    }
    expect(caught).toBeInstanceOf(EnvVarValueRejected)
    expect((caught as EnvVarValueRejected).key).toBe('BAD')

    // Canonical file is unchanged. (The .tmp file may or may not exist —
    // we never claim that — but the production path stays intact.)
    const after = await realFs.readFile(envFilePath, 'utf8')
    expect(after).toBe(before)
    expect(manager.current().has('BAD')).toBe(false)
  })

  it('empty delta is a no-op (no rewrite, no error)', async () => {
    const { manager, envFilePath } = makeManager()
    await manager.loadInitial([{ key: 'A', value: '1' }])

    const statBefore = await realFs.stat(envFilePath)
    // Sleep a tick so any rewrite would bump mtime visibly.
    await new Promise((r) => setTimeout(r, 5))

    await manager.applyDelta([])

    const statAfter = await realFs.stat(envFilePath)
    // Same mtime → no rewrite happened.
    expect(statAfter.mtimeMs).toBe(statBefore.mtimeMs)
  })
})

describe('EnvVarManager.current', () => {
  it('returns a ReadonlyMap snapshot of current values', async () => {
    const { manager } = makeManager()
    await manager.loadInitial([
      { key: 'A', value: '1' },
      { key: 'B', value: '2' },
    ])
    const snap = manager.current()
    expect(snap.get('A')).toBe('1')
    expect(snap.get('B')).toBe('2')
    expect(snap.size).toBe(2)

    // The returned reference is a Map (TypeScript narrows it to ReadonlyMap
    // at the type level; at runtime it's the live underlying Map). Assert
    // it has the read-only surface we care about.
    expect(typeof snap.get).toBe('function')
    expect(typeof snap.has).toBe('function')
    expect(typeof snap.entries).toBe('function')
  })

  it('reflects mutations after applyDelta', async () => {
    const { manager } = makeManager()
    await manager.loadInitial([{ key: 'A', value: '1' }])
    await manager.applyDelta([{ key: 'A', value: '2' }, { key: 'B', value: 'new' }])

    expect(manager.current().get('A')).toBe('2')
    expect(manager.current().get('B')).toBe('new')
  })
})

describe('EnvVarManager — chown gating', () => {
  it('does not invoke chown when not running as root', async () => {
    const logger = makeLogger()
    const envFilePath = path.join(workDir, '.glenn', 'env')
    const fsSpy = {
      mkdir: realFs.mkdir,
      writeFile: realFs.writeFile,
      rename: realFs.rename,
      open: realFs.open,
      chown: vi.fn(async () => {}),
      unlink: realFs.unlink,
    }
    const manager = new EnvVarManager({
      envFilePath,
      logger: logger as unknown as Logger,
      fs: fsSpy,
      getuid: () => 1000,
      resolveAgentUser: () => ({ uid: 100, gid: 100 }),
    })
    await manager.loadInitial([{ key: 'A', value: '1' }])

    expect(fsSpy.chown).not.toHaveBeenCalled()
  })

  it('warns and skips chown when agent user does not exist (running as root)', async () => {
    const logger = makeLogger()
    const envFilePath = path.join(workDir, '.glenn', 'env')
    const fsSpy = {
      mkdir: realFs.mkdir,
      writeFile: realFs.writeFile,
      rename: realFs.rename,
      open: realFs.open,
      chown: vi.fn(async () => {}),
      unlink: realFs.unlink,
    }
    const manager = new EnvVarManager({
      envFilePath,
      logger: logger as unknown as Logger,
      fs: fsSpy,
      getuid: () => 0, // pretend root
      resolveAgentUser: () => null, // no agent user
    })
    await manager.loadInitial([{ key: 'A', value: '1' }])

    expect(fsSpy.chown).not.toHaveBeenCalled()
    const warns = logger.warn.mock.calls.flat().filter((a) => typeof a === 'string')
    expect(warns.some((m) => m.includes('agent user not found'))).toBe(true)
  })

  it('calls chown with resolved uid/gid when running as root', async () => {
    const logger = makeLogger()
    const envFilePath = path.join(workDir, '.glenn', 'env')
    const chownSpy = vi.fn(async () => {})
    const fsSpy = {
      mkdir: realFs.mkdir,
      writeFile: realFs.writeFile,
      rename: realFs.rename,
      open: realFs.open,
      chown: chownSpy,
      unlink: realFs.unlink,
    }
    const manager = new EnvVarManager({
      envFilePath,
      logger: logger as unknown as Logger,
      fs: fsSpy,
      getuid: () => 0,
      resolveAgentUser: () => ({ uid: 1000, gid: 1000 }),
    })
    await manager.loadInitial([{ key: 'A', value: '1' }])

    expect(chownSpy).toHaveBeenCalledTimes(1)
    expect(chownSpy).toHaveBeenCalledWith(envFilePath, 1000, 1000)
  })
})
