// Tests for FileChangeWatcher. Most tests use a fake watcher factory so we can
// drive FS events deterministically; one integration test exercises real
// chokidar against a temp dir to prove the wiring.

import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { Logger } from 'pino'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  FileChangeWatcher,
  type FileChangeWatcherOptions,
  type PatternHook,
  type WatcherFactory,
  type WatcherHandle,
} from './FileChangeWatcher.js'

// ============================================================================
// Test helpers
// ============================================================================

type Handler = (arg?: string) => void

class FakeWatcher implements WatcherHandle {
  readonly #handlers = new Map<string, Handler>()
  closed = false

  on(event: 'add' | 'change' | 'unlink' | 'ready', handler: Handler): this {
    this.#handlers.set(event, handler)
    return this
  }

  close(): Promise<void> {
    this.closed = true
    return Promise.resolve()
  }

  fire(event: 'add' | 'change' | 'unlink', path: string): void {
    this.#handlers.get(event)?.(path)
  }

  ready(): void {
    this.#handlers.get('ready')?.()
  }

  hasReadyHandler(): boolean {
    return this.#handlers.has('ready')
  }
}

interface FactoryHarness {
  factory: WatcherFactory
  /** The most recently created FakeWatcher. */
  current: () => FakeWatcher
  /** Auto-call `.ready()` synchronously after construction. */
  setAutoReady: (auto: boolean) => void
}

function makeFactoryHarness(): FactoryHarness {
  let current: FakeWatcher | null = null
  let autoReady = true
  const factory: WatcherFactory = () => {
    const w = new FakeWatcher()
    current = w
    if (autoReady) {
      // The ready handler is registered synchronously inside
      // FileChangeWatcher.start, so we must defer the ready emission to a
      // microtask so the listener is in place by the time we fire.
      queueMicrotask(() => w.ready())
    }
    return w
  }
  return {
    factory,
    current: () => {
      if (!current) throw new Error('factory not yet invoked')
      return current
    },
    setAutoReady: (auto) => {
      autoReady = auto
    },
  }
}

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

interface BuildResult {
  watcher: FileChangeWatcher
  harness: FactoryHarness
  batches: { pattern: string; hookName: string; changedFiles: string[] }[]
  logger: ReturnType<typeof makeLogger>
}

function build(
  opts: Partial<FileChangeWatcherOptions> = {},
  hooks: PatternHook[] = [],
): BuildResult {
  const harness = makeFactoryHarness()
  const logger = makeLogger()
  const watcher = new FileChangeWatcher({
    rootDir: opts.rootDir ?? '/data/project/repo',
    debounceMs: opts.debounceMs ?? 2_000,
    logger: logger as unknown as Logger,
    watcher: harness.factory,
  })
  const batches: { pattern: string; hookName: string; changedFiles: string[] }[] = []
  watcher.on('changeBatch', (batch) => batches.push(batch))
  if (hooks.length > 0) watcher.setHooks(hooks)
  return { watcher, harness, batches, logger }
}

// ============================================================================
// Tests with fake watcher + fake timers
// ============================================================================

describe('FileChangeWatcher', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  describe('without hooks', () => {
    it('emits no events when no hooks are registered, even on FS bursts', async () => {
      const { watcher, harness, batches } = build()
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/a.tsx')
      w.fire('change', '/data/project/repo/b.tsx')
      w.fire('add', '/data/project/repo/c.ts')
      w.fire('unlink', '/data/project/repo/d.tsx')
      w.fire('change', '/data/project/repo/e.tsx')

      await vi.advanceTimersByTimeAsync(5_000)

      expect(batches).toEqual([])
      await watcher.stop()
    })
  })

  describe('single hook', () => {
    it('emits one batch for a single matching file after debounce', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tc', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')

      // Before debounce expires: nothing emitted.
      await vi.advanceTimersByTimeAsync(1_999)
      expect(batches).toEqual([])

      // After full debounce window: one batch.
      await vi.advanceTimersByTimeAsync(1)
      expect(batches).toHaveLength(1)
      expect(batches[0]).toEqual({
        pattern: '**/*.tsx',
        hookName: 'tc',
        changedFiles: ['src/foo.tsx'],
      })

      await watcher.stop()
    })

    it('ignores non-matching paths', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tc', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.ts')

      await vi.advanceTimersByTimeAsync(5_000)
      expect(batches).toEqual([])
      await watcher.stop()
    })

    it('collapses bursts of events into a single batch (deduped)', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tc', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      // 50 rapid events for the same path, 10ms apart (< debounceMs).
      for (let i = 0; i < 50; i++) {
        w.fire('change', '/data/project/repo/src/foo.tsx')
        await vi.advanceTimersByTimeAsync(10)
      }
      // No batch yet — the timer keeps resetting.
      expect(batches).toEqual([])

      // Quiesce: advance past the debounce window from the last event.
      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(1)
      expect(batches[0]).toEqual({
        pattern: '**/*.tsx',
        hookName: 'tc',
        changedFiles: ['src/foo.tsx'],
      })

      await watcher.stop()
    })

    it('treats add/unlink the same as change', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tc', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('add', '/data/project/repo/src/new.tsx')
      w.fire('unlink', '/data/project/repo/src/old.tsx')

      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(1)
      expect(batches[0]?.changedFiles.sort()).toEqual(['src/new.tsx', 'src/old.tsx'])

      await watcher.stop()
    })
  })

  describe('per-hook debounce', () => {
    it('two hooks with non-overlapping patterns each get their own batch', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tsx', pattern: '**/*.tsx' },
        { name: 'css', pattern: '**/*.css' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/a.tsx')
      await vi.advanceTimersByTimeAsync(500)
      w.fire('change', '/data/project/repo/src/b.css')
      await vi.advanceTimersByTimeAsync(500)
      w.fire('change', '/data/project/repo/src/c.tsx')
      await vi.advanceTimersByTimeAsync(500)
      w.fire('change', '/data/project/repo/src/d.css')

      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(2)
      const tsxBatch = batches.find((b) => b.hookName === 'tsx')
      const cssBatch = batches.find((b) => b.hookName === 'css')
      expect(tsxBatch?.changedFiles.sort()).toEqual(['src/a.tsx', 'src/c.tsx'])
      expect(cssBatch?.changedFiles.sort()).toEqual(['src/b.css', 'src/d.css'])

      await watcher.stop()
    })

    it('the same path matched by two hooks notifies both independently', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'h1', pattern: '**/*.tsx' },
        { name: 'h2', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')

      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(2)
      const names = batches.map((b) => b.hookName).sort()
      expect(names).toEqual(['h1', 'h2'])
      for (const batch of batches) {
        expect(batch.changedFiles).toEqual(['src/foo.tsx'])
        expect(batch.pattern).toBe('**/*.tsx')
      }

      await watcher.stop()
    })
  })

  describe('setHooks hot-swap', () => {
    it('drops pending events for hooks no longer present', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'A', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')
      // Don't advance past the debounce window yet.
      await vi.advanceTimersByTimeAsync(500)

      // Hot-swap: replace A with B (different name).
      watcher.setHooks([{ name: 'B', pattern: '**/*.css' }])

      // Advance well past the original debounce window — A's pending state
      // must have been dropped, so no batch for A.
      await vi.advanceTimersByTimeAsync(5_000)

      expect(batches).toEqual([])

      await watcher.stop()
    })

    it('preserves pending events for retained hooks', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'A', pattern: '**/*.tsx' },
        { name: 'B', pattern: '**/*.css' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')
      await vi.advanceTimersByTimeAsync(500)

      // Replace [A, B] with [A, C] — A is retained, B removed, C added.
      watcher.setHooks([
        { name: 'A', pattern: '**/*.tsx' },
        { name: 'C', pattern: '**/*.md' },
      ])

      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(1)
      expect(batches[0]).toEqual({
        pattern: '**/*.tsx',
        hookName: 'A',
        changedFiles: ['src/foo.tsx'],
      })

      await watcher.stop()
    })

    it('newly added hooks become active immediately', async () => {
      const { watcher, harness, batches } = build()
      await watcher.start()
      const w = harness.current()

      // No hooks yet — fire an event that goes nowhere.
      w.fire('change', '/data/project/repo/src/foo.tsx')
      await vi.advanceTimersByTimeAsync(2_000)
      expect(batches).toEqual([])

      // Now register a hook and fire again.
      watcher.setHooks([{ name: 'tc', pattern: '**/*.tsx' }])
      w.fire('change', '/data/project/repo/src/bar.tsx')
      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(1)
      expect(batches[0]?.changedFiles).toEqual(['src/bar.tsx'])

      await watcher.stop()
    })
  })

  describe('stop()', () => {
    it('cancels pending batches without flushing', async () => {
      const { watcher, harness, batches } = build({}, [
        { name: 'tc', pattern: '**/*.tsx' },
      ])
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')
      await vi.advanceTimersByTimeAsync(500)

      // Stop before the debounce window expires — pending should be discarded.
      await watcher.stop()

      // Advance well past — no late batch should appear.
      await vi.advanceTimersByTimeAsync(5_000)
      expect(batches).toEqual([])
    })
  })

  describe('relative path computation', () => {
    it('strips the rootDir prefix when emitting', async () => {
      const { watcher, harness, batches } = build(
        { rootDir: '/data/project/repo' },
        [{ name: 'tc', pattern: '**/*.tsx' }],
      )
      await watcher.start()
      const w = harness.current()

      w.fire('change', '/data/project/repo/src/foo.tsx')
      await vi.advanceTimersByTimeAsync(2_000)

      expect(batches).toHaveLength(1)
      expect(batches[0]?.changedFiles).toEqual(['src/foo.tsx'])
      // No leading slash.
      expect(batches[0]?.changedFiles[0]?.startsWith('/')).toBe(false)

      await watcher.stop()
    })
  })

  describe('start() readiness', () => {
    it('does not resolve until ready fires', async () => {
      // Real timers here: start() awaits readFile('.gitignore') which is a
      // real fs op and needs the macrotask queue to actually pump. Fake timers
      // shadow that and break the test ordering.
      vi.useRealTimers()

      const harness = makeFactoryHarness()
      harness.setAutoReady(false)
      const logger = makeLogger()
      const watcher = new FileChangeWatcher({
        rootDir: '/data/project/repo',
        debounceMs: 2_000,
        logger: logger as unknown as Logger,
        watcher: harness.factory,
      })

      let resolved = false
      const startPromise = watcher.start().then(() => {
        resolved = true
      })

      // Wait until the factory has been invoked (i.e. start() has finished
      // the .gitignore lookup and constructed the watcher handle). We can't
      // know the exact number of microtasks needed — it depends on how fast
      // ENOENT from readFile propagates — so poll briefly.
      await new Promise<void>((resolve, reject) => {
        const t = setTimeout(() => reject(new Error('factory never invoked')), 1_000)
        const check = () => {
          try {
            harness.current()
            clearTimeout(t)
            resolve()
          } catch {
            setTimeout(check, 5)
          }
        }
        check()
      })

      // Factory has run; ready has NOT fired yet, so the promise must still
      // be pending.
      expect(resolved).toBe(false)

      // Fire ready — start() resolves.
      harness.current().ready()
      await startPromise
      expect(resolved).toBe(true)

      await watcher.stop()
    })
  })
})

// ============================================================================
// Integration test against real chokidar
// ============================================================================

describe('FileChangeWatcher (integration with real chokidar)', () => {
  // Real timers — chokidar uses real FS APIs and we need real setTimeout.
  it('emits a batch when a file is written under the watched directory', async () => {
    const tmp = await mkdtemp(join(tmpdir(), 'fcw-int-'))
    try {
      const logger = makeLogger()
      const watcher = new FileChangeWatcher({
        rootDir: tmp,
        debounceMs: 50,
        logger: logger as unknown as Logger,
      })
      const batches: { pattern: string; hookName: string; changedFiles: string[] }[] = []
      watcher.on('changeBatch', (b) => batches.push(b))
      watcher.setHooks([{ name: 'tsx', pattern: '**/*.tsx' }])

      await watcher.start()

      // Write a matching file.
      const target = join(tmp, 'foo.tsx')
      await writeFile(target, 'export const x = 1')

      // Wait for the batch — debounce is 50ms; allow generous slack.
      await new Promise<void>((resolve, reject) => {
        const t = setTimeout(() => reject(new Error('timed out waiting for batch')), 5_000)
        const check = () => {
          if (batches.length > 0) {
            clearTimeout(t)
            resolve()
          } else {
            setTimeout(check, 25)
          }
        }
        check()
      })

      expect(batches.length).toBeGreaterThanOrEqual(1)
      const first = batches[0]!
      expect(first.hookName).toBe('tsx')
      expect(first.pattern).toBe('**/*.tsx')
      expect(first.changedFiles).toContain('foo.tsx')

      await watcher.stop()
    } finally {
      await rm(tmp, { recursive: true, force: true })
    }
  }, 15_000)
})
