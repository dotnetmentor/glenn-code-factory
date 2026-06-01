// Tests for SshKeyHandler. We use a real tempdir per test (mkdtemp under
// os.tmpdir()) so we exercise the actual fs/promises paths end-to-end —
// permissions, atomic rename, idempotency. The only seam we lean on is the
// `fs` injection for the chmod-failure-swallow test, where we wrap real fs
// and override `chmod` once. Everything else is real I/O.
//
// No node:fs mocks; no fake timers needed (the handler has no timers).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as realFs from 'node:fs/promises'
import * as os from 'node:os'
import path from 'node:path'
import type { Logger } from 'pino'

import { SshKeyHandler, type SshKeyHandlerFs } from './SshKeyHandler.js'

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

const PEM_BODY =
  '-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAAB\n-----END OPENSSH PRIVATE KEY-----\n'

const PEM_BODY_ROTATED =
  '-----BEGIN OPENSSH PRIVATE KEY-----\nROTATEDxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n-----END OPENSSH PRIVATE KEY-----\n'

let homeDir: string

beforeEach(async () => {
  homeDir = await realFs.mkdtemp(path.join(os.tmpdir(), 'ssh-key-handler-'))
})

afterEach(async () => {
  // Best-effort cleanup. Tests should leave the dir in a sane shape; if not,
  // rm recursive picks it up. force:true swallows ENOENT.
  await realFs.rm(homeDir, { recursive: true, force: true })
})

function makeHandler(overrides: { fs?: SshKeyHandlerFs } = {}) {
  const logger = makeLogger()
  const handler = new SshKeyHandler({
    homeDir,
    logger: logger as unknown as Logger,
    ...(overrides.fs !== undefined ? { fs: overrides.fs } : {}),
  })
  return { handler, logger }
}

const keyPath = (): string => path.join(homeDir, '.ssh', 'id_ed25519')
const tmpPath = (): string => path.join(homeDir, '.ssh', 'id_ed25519.tmp')
const configPath = (): string => path.join(homeDir, '.ssh', 'config')
const sshDirPath = (): string => path.join(homeDir, '.ssh')

// ============================================================================
// Tests
// ============================================================================

describe('SshKeyHandler.applyConfig', () => {
  describe('fresh install', () => {
    it('writes id_ed25519 with the PEM body and mode 0o600', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      const written = await realFs.readFile(keyPath(), 'utf8')
      expect(written).toBe(PEM_BODY)

      const stat = await realFs.stat(keyPath())
      expect(stat.mode & 0o777).toBe(0o600)
    })

    it('creates ~/.ssh with mode 0o700', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      const stat = await realFs.stat(sshDirPath())
      expect(stat.isDirectory()).toBe(true)
      expect(stat.mode & 0o777).toBe(0o700)
    })

    it('writes ~/.ssh/config with the expected three-line content', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      const content = await realFs.readFile(configPath(), 'utf8')
      const expected = `Host github.com\n  IdentityFile ${keyPath()}\n  StrictHostKeyChecking accept-new\n`
      expect(content).toBe(expected)
    })

    it('does not leave a tmp file behind on success', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      await expect(realFs.access(tmpPath())).rejects.toMatchObject({ code: 'ENOENT' })
    })

    it('logs "deploy key installed" without including the PEM body', async () => {
      const { handler, logger } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      expect(logger.info).toHaveBeenCalledWith('deploy key installed')

      // Walk every info call and assert no argument carries the PEM body.
      for (const call of logger.info.mock.calls) {
        for (const arg of call) {
          const serialised = typeof arg === 'string' ? arg : JSON.stringify(arg)
          expect(serialised).not.toContain('BEGIN OPENSSH PRIVATE KEY')
        }
      }
    })
  })

  describe('idempotency', () => {
    it('a second call with the same key body is a no-op (logs debug, content unchanged)', async () => {
      const { handler, logger } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })

      const statBefore = await realFs.stat(keyPath())

      // Wait a beat so any rewrite would bump mtime — fs mtime resolution is
      // typically 1ms but some FS only have second-resolution. We compare both
      // mtime and content as belt-and-braces.
      await new Promise((r) => setTimeout(r, 5))

      logger.debug.mockClear()
      await handler.applyConfig({ deployKey: PEM_BODY })

      const statAfter = await realFs.stat(keyPath())
      const contentAfter = await realFs.readFile(keyPath(), 'utf8')

      expect(contentAfter).toBe(PEM_BODY)
      expect(statAfter.mtimeMs).toBe(statBefore.mtimeMs)
      expect(logger.debug).toHaveBeenCalledWith('deploy key unchanged; no-op')
    })
  })

  describe('rotation', () => {
    it('replaces the key body and preserves mode 0o600', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })
      await handler.applyConfig({ deployKey: PEM_BODY_ROTATED })

      const content = await realFs.readFile(keyPath(), 'utf8')
      expect(content).toBe(PEM_BODY_ROTATED)

      const stat = await realFs.stat(keyPath())
      expect(stat.mode & 0o777).toBe(0o600)
    })
  })

  describe('null/undefined deploy key', () => {
    it('writes nothing when no key was previously installed', async () => {
      const { handler, logger } = makeHandler()
      await handler.applyConfig({ deployKey: null })

      await expect(realFs.access(keyPath())).rejects.toMatchObject({ code: 'ENOENT' })
      await expect(realFs.access(configPath())).rejects.toMatchObject({ code: 'ENOENT' })
      expect(logger.debug).toHaveBeenCalledWith(
        'no deploy key in config; leaving existing key alone',
      )
    })

    it('preserves an existing key when called with null', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })
      await handler.applyConfig({ deployKey: null })

      const content = await realFs.readFile(keyPath(), 'utf8')
      expect(content).toBe(PEM_BODY)
    })

    it('treats undefined deployKey the same as null (leave alone)', async () => {
      const { handler } = makeHandler()
      await handler.applyConfig({ deployKey: PEM_BODY })
      await handler.applyConfig({})

      const content = await realFs.readFile(keyPath(), 'utf8')
      expect(content).toBe(PEM_BODY)
    })
  })

  describe('chmod failure on post-rename safety call', () => {
    it('swallows the error and logs a warn (key still on disk)', async () => {
      // Wrap real fs; have chmod reject the FIRST call against the *key
      // path* (not the .ssh dir) so the post-rename belt-and-braces chmod
      // exercises the swallow path. The dir chmod runs first and must
      // succeed — otherwise we'd just be testing the dir branch.
      let keyChmodCalls = 0
      const wrappedFs: SshKeyHandlerFs = {
        mkdir: realFs.mkdir,
        readFile: realFs.readFile,
        writeFile: realFs.writeFile,
        rename: realFs.rename,
        unlink: realFs.unlink,
        chmod: vi.fn(async (p: Parameters<typeof realFs.chmod>[0], mode: Parameters<typeof realFs.chmod>[1]) => {
          // Detect the key-path chmod (not the .ssh dir chmod) and reject once.
          if (typeof p === 'string' && p.endsWith('id_ed25519')) {
            keyChmodCalls++
            if (keyChmodCalls === 1) {
              throw new Error('synthetic chmod failure')
            }
          }
          return realFs.chmod(p, mode)
        }) as unknown as typeof realFs.chmod,
      }

      const { handler, logger } = makeHandler({ fs: wrappedFs })

      await expect(handler.applyConfig({ deployKey: PEM_BODY })).resolves.toBeUndefined()

      // Key is still on disk (writeFile happened before the chmod).
      const content = await realFs.readFile(keyPath(), 'utf8')
      expect(content).toBe(PEM_BODY)

      // Warn was logged; matched on message substring so we don't bind to
      // exact pino object shape.
      const warnedAboutChmod = logger.warn.mock.calls.some((args) =>
        args.some(
          (a) => typeof a === 'string' && a.includes('chmod') && a.includes('deploy key'),
        ),
      )
      expect(warnedAboutChmod).toBe(true)
    })
  })
})
