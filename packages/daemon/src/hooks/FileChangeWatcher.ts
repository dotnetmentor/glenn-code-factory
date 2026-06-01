// FileChangeWatcher — Card 7 of the daemon-hooks-runner spec.
//
// Watches the project repo via a single chokidar instance, matches each FS
// event against per-hook glob patterns (compiled with picomatch), and emits a
// single debounced `changeBatch` per hook once the burst quiets down.
//
// Strict scope: NO executor calls, NO SignalR, NO module wiring. The Card 9
// self-heal coordinator and Card 10 main wiring choose what to do with the
// emitted batches; the watcher just produces them.
//
// Design notes worth keeping near the code:
//
//   - One chokidar instance covering rootDir. Hooks are layered on top via
//     in-process pattern matching — we do NOT spin up a watcher per hook.
//     Universal noise (node_modules, .git, dist, build, .next, target, bin,
//     obj, __pycache__, .venv, etc.) is hardcoded into the ignored list
//     because no caller reasonably wants to watch those — and on Linux they
//     would otherwise rapidly exhaust the inotify watch limit (ENOSPC) on
//     freshly-cloned monorepos with hundreds of nested node_modules.
//
//   - On top of the hardcoded list we also honor the project's root
//     `.gitignore` (parsed with the `ignore` npm package, which implements
//     real gitignore syntax). Only the root .gitignore is honored — nested
//     .gitignores would require dynamic re-parsing per directory, which is
//     more complex than the watcher needs. This is best-effort noise
//     reduction, not a full git-semantics implementation.
//
//   - Debounce is per-hook (keyed by hook name, not pattern: two hooks could
//     share a pattern with different names and must debounce independently).
//     Each hook has its own pending Set + timer. A burst of 50 events for one
//     hook collapses to one batch; events targeting two hooks emit two batches.
//
//   - Hot-swap (`setHooks`): replace the registry, recompile matchers, drop
//     pending state for hooks no longer present. Removed hooks lose their
//     pending events — discarding is the right shutdown semantics; flushing
//     stale config would be worse.
//
//   - `stop()` clears timers and pending Sets without flushing. The daemon is
//     going down; emitting a final batch nobody is wired to receive would be
//     pointless and could mislead listeners.
//
//   - Path matching uses paths RELATIVE to rootDir. Picomatch's `**/*.tsx`
//     style globs assume relative inputs; absolute paths starting with `/`
//     would never match. Chokidar emits absolute paths when given an absolute
//     root, so we strip the prefix before matching and emitting.

import { EventEmitter } from 'node:events'
import { readFile } from 'node:fs/promises'
import { join, relative } from 'node:path'
import chokidar from 'chokidar'
import ignore from 'ignore'
import picomatch from 'picomatch'
import type { Logger } from 'pino'

export interface FileChangeWatcherOptions {
  rootDir: string
  /** Default 2000 ms. Per-hook debounce window. */
  debounceMs?: number
  logger: Logger
  /** Test seam — defaults to chokidar.watch. */
  watcher?: WatcherFactory
}

export type WatcherFactory = (
  rootDir: string,
  opts: { ignored: ((path: string) => boolean)[] },
) => WatcherHandle

export interface WatcherHandle {
  // chokidar's real surface accepts more events; we wire add/change/unlink/
  // ready + error. The ready handler takes no arguments; add/change/unlink
  // take a path; error takes an Error. We type the handler arg as an optional
  // unknown so a single signature covers all three shapes — callers narrow
  // at the use site.
  on(
    event: 'add' | 'change' | 'unlink' | 'ready' | 'error',
    handler: (arg?: unknown) => void,
  ): WatcherHandle
  close(): Promise<void>
}

export interface FileChangeWatcherEvents {
  changeBatch: [{ pattern: string; hookName: string; changedFiles: string[] }]
}

export interface PatternHook {
  name: string
  pattern: string
}

const DEFAULT_DEBOUNCE_MS = 2_000

// Universal noise. Hardcoded — no caller reasonably wants to watch these in a
// project repo (e.g. node_modules, .git, build outputs, language caches), and
// exposing them as configuration would just invite footguns. This list is
// intentionally broad: on Linux, watching even a fraction of a freshly-cloned
// monorepo's nested node_modules will hit the inotify watch limit (ENOSPC) and
// crash the daemon. The companion `ignore`-parsed .gitignore (loaded in
// start()) layers project-specific ignores on top of this baseline.
//
// Documented in glob form (`**/foo/**`) for human readers familiar with
// picomatch syntax; the runtime matcher uses the `ignore` package, which
// expects gitignore syntax — bare paths like `node_modules` match at any
// depth automatically. See `HARDCODED_IGNORED_GITIGNORE` below.
const HARDCODED_IGNORED: string[] = [
  '**/node_modules/**',
  '**/.git/**',
  '**/dist/**',
  '**/build/**',
  '**/.next/**',
  '**/.nuxt/**',
  '**/.cache/**',
  '**/.turbo/**',
  '**/.parcel-cache/**',
  '**/.vite/**',
  '**/coverage/**',
  '**/.pytest_cache/**',
  '**/__pycache__/**',
  '**/target/**',
  '**/bin/**',
  '**/obj/**',
  '**/.venv/**',
  '**/venv/**',
]

// Gitignore-syntax equivalents of HARDCODED_IGNORED, derived by stripping the
// `**/` prefix and `/**` suffix. Used to seed the `ignore` package matcher,
// which expects gitignore syntax (a bare path like `node_modules` matches at
// any depth).
const HARDCODED_IGNORED_GITIGNORE: string[] = HARDCODED_IGNORED.map((glob) =>
  glob.replace(/^\*\*\//, '').replace(/\/\*\*$/, ''),
)

/** Default factory: thin adapter around chokidar.watch matching WatcherHandle. */
const defaultWatcherFactory: WatcherFactory = (rootDir, opts) => {
  // chokidar 4.x accepts function matchers for `ignored` — each is called with
  // every candidate path and must return true to skip it. We pass through the
  // function array directly.
  const w = chokidar.watch(rootDir, { ignored: opts.ignored, ignoreInitial: true })
  return w as unknown as WatcherHandle
}

export class FileChangeWatcher extends EventEmitter<FileChangeWatcherEvents> {
  readonly #rootDir: string
  readonly #debounceMs: number
  readonly #logger: Logger
  readonly #watcherFactory: WatcherFactory

  #hooks: PatternHook[] = []
  #matchers: Map<string, (path: string) => boolean> = new Map()
  #pending: Map<string, Set<string>> = new Map()
  #timers: Map<string, NodeJS.Timeout> = new Map()
  #chokidar: WatcherHandle | null = null
  #started = false

  constructor(opts: FileChangeWatcherOptions) {
    super()
    this.#rootDir = opts.rootDir
    this.#debounceMs = opts.debounceMs ?? DEFAULT_DEBOUNCE_MS
    this.#logger = opts.logger.child({ module: 'file-change-watcher' })
    this.#watcherFactory = opts.watcher ?? defaultWatcherFactory
  }

  /**
   * Replace the active hook registry. Hot-swappable: matchers are recompiled,
   * pending state for removed hooks is dropped (timers cleared, sets erased),
   * and retained hooks keep their pending state.
   */
  setHooks(hooks: PatternHook[]): void {
    const nextNames = new Set(hooks.map((h) => h.name))

    // Drop pending state for hooks no longer present. Removed hooks discard
    // their pending events on hook removal — flushing would surface a batch
    // for a hook that may have been deliberately disabled.
    for (const name of [...this.#timers.keys()]) {
      if (!nextNames.has(name)) {
        const timer = this.#timers.get(name)
        if (timer) clearTimeout(timer)
        this.#timers.delete(name)
        this.#pending.delete(name)
      }
    }

    // Rebuild matchers. Picomatch compilation is cheap, and recompiling on
    // every setHooks keeps the implementation trivial.
    const nextMatchers = new Map<string, (path: string) => boolean>()
    for (const hook of hooks) {
      nextMatchers.set(hook.name, picomatch(hook.pattern))
    }

    this.#hooks = hooks
    this.#matchers = nextMatchers

    this.#logger.info({ count: hooks.length }, 'hooks updated')
  }

  async start(): Promise<void> {
    if (this.#started) return
    this.#started = true

    // Build the ignore matcher. We layer two sources:
    //   1. The hardcoded universal-noise list (translated to gitignore syntax).
    //   2. The project's root .gitignore, if present.
    // The `ignore` package handles gitignore semantics correctly (negation,
    // anchored vs unanchored patterns, etc.) so we don't have to.
    const ig = ignore()
    ig.add(HARDCODED_IGNORED_GITIGNORE)

    try {
      const gitignoreContent = await readFile(join(this.#rootDir, '.gitignore'), 'utf8')
      ig.add(gitignoreContent)
      this.#logger.info({ rootDir: this.#rootDir }, 'gitignore loaded for watcher')
    } catch (err) {
      // ENOENT is expected for projects without a .gitignore. Anything else is
      // surprising but not fatal — we still have the hardcoded list.
      if ((err as NodeJS.ErrnoException).code !== 'ENOENT') {
        this.#logger.warn(
          { err },
          'failed to read .gitignore, continuing with hardcoded patterns only',
        )
      }
    }

    // Build the chokidar `ignored` callback. chokidar 4.x calls this for every
    // path it considers; returning true means "skip". The `ignore` package
    // expects POSIX-style paths relative to the gitignore root and without a
    // leading slash. chokidar also asks about the root itself (rel === '') —
    // never skip the root or we won't watch anything.
    const rootDir = this.#rootDir
    const ignoredFn = (absolutePath: string): boolean => {
      const rel = relative(rootDir, absolutePath)
      if (rel === '' || rel.startsWith('..')) return false
      return ig.ignores(rel)
    }

    const handle = this.#watcherFactory(this.#rootDir, { ignored: [ignoredFn] })
    this.#chokidar = handle

    handle.on('add', (p) => this.#onFsEvent(typeof p === 'string' ? p : undefined))
    handle.on('change', (p) => this.#onFsEvent(typeof p === 'string' ? p : undefined))
    handle.on('unlink', (p) => this.#onFsEvent(typeof p === 'string' ? p : undefined))

    // CRITICAL: an unhandled 'error' event from chokidar kills the daemon.
    //
    // Chokidar emits 'error' for per-file fs.watch() failures (EACCES, ENOENT,
    // EPERM, the inotify ENOSPC ceiling). Without a listener, the underlying
    // EventEmitter either throws synchronously OR — when the failure happens
    // inside chokidar's async recursive-add phase — bubbles up as an
    // unhandledRejection. Either way the daemon process dies. A repo with a
    // single permission-restricted file (e.g. test fixtures, .ssh/, vault
    // keys checked in via git-crypt) is enough to crash-loop the runtime.
    //
    // Per-file errors are not fatal to the watcher as a whole — chokidar keeps
    // watching everything else. We log and swallow.
    handle.on('error', (err) => {
      const e = err instanceof Error ? err : new Error(String(err))
      const errno = (e as NodeJS.ErrnoException).code
      this.#logger.warn(
        { err: e, errno },
        'chokidar emitted error (continuing) — usually a single-file EACCES/ENOENT/ENOSPC',
      )
    })

    // Wait for chokidar's `ready` event. Some factories (notably test fakes)
    // may never emit it — fall back to resolving when start() returns by
    // racing against a microtask-scheduled "no ready" signal? No: per the
    // brief, we resolve when ready fires. If the factory never fires it, the
    // caller is responsible for the test seam. Real chokidar always does.
    await new Promise<void>((resolve) => {
      handle.on('ready', () => resolve())
    })

    this.#logger.info({ rootDir: this.#rootDir }, 'file watcher started')
  }

  async stop(): Promise<void> {
    if (!this.#started) return
    this.#started = false

    // Clear all timers + pending sets WITHOUT flushing. Daemon shutdown
    // semantics: discard. Listeners may already be gone.
    for (const timer of this.#timers.values()) {
      clearTimeout(timer)
    }
    this.#timers.clear()
    this.#pending.clear()

    if (this.#chokidar) {
      await this.#chokidar.close()
      this.#chokidar = null
    }

    this.#logger.info('file watcher stopped')
  }

  #onFsEvent(absolutePath: string | undefined): void {
    if (typeof absolutePath !== 'string') return

    // chokidar may emit either absolute or relative paths depending on how it
    // was started; `relative()` handles both — when given a relative path as
    // the second arg it resolves both against cwd, which yields the same
    // input shape we want.
    const rel = relative(this.#rootDir, absolutePath)

    for (const hook of this.#hooks) {
      const matcher = this.#matchers.get(hook.name)
      if (!matcher) continue
      if (!matcher(rel)) continue

      this.#logger.debug({ path: rel, hook: hook.name }, 'file event matched hook')

      let pending = this.#pending.get(hook.name)
      if (!pending) {
        pending = new Set<string>()
        this.#pending.set(hook.name, pending)
      }
      pending.add(rel)

      // Reset the per-hook debounce timer. Each event in a burst pushes the
      // emit further out; once events stop for `debounceMs`, the timer fires.
      const existing = this.#timers.get(hook.name)
      if (existing) clearTimeout(existing)

      const timer = setTimeout(() => {
        this.#flushHook(hook)
      }, this.#debounceMs)
      // unref so a pending timer doesn't keep the event loop alive on its
      // own — the daemon's lifecycle is managed elsewhere.
      timer.unref?.()
      this.#timers.set(hook.name, timer)
    }
  }

  #flushHook(hook: PatternHook): void {
    const pending = this.#pending.get(hook.name)
    this.#timers.delete(hook.name)
    this.#pending.delete(hook.name)

    if (!pending || pending.size === 0) return

    const changedFiles = Array.from(pending)
    this.#logger.debug(
      { hookName: hook.name, count: changedFiles.length },
      'emitting changeBatch',
    )
    this.emit('changeBatch', { pattern: hook.pattern, hookName: hook.name, changedFiles })
  }
}
