// EnvVarManager — Card 7 of project-secrets (Spec 14).
//
// Owns the on-disk env-var snapshot at `<envFilePath>` (default
// `/data/.glenn/env`). Inputs are:
//
//   - `loadInitial(entries)` — bootstrap-time replace. Clears the in-memory
//     map and rewrites the file with `entries` verbatim (sorted on disk).
//   - `applyDelta(delta)`    — runtime delta from UpdateConfig. Each entry is
//     either a key/value pair (set/replace) or `value: null` (delete).
//
// SECURITY: never log values — keys-only.
//
// Disk format (deterministic, easy to diff/audit):
//
//   sortedKey1=value1\n
//   sortedKey2=value2\n
//   …
//
// Atomicity:
//   1. mkdir -p parent (mode 0o700)
//   2. write `<path>.tmp` with mode 0o600
//   3. fsync the tmp file (open+sync+close)
//   4. rename(tmp, path) — atomic on POSIX
//   5. best-effort fsync the parent dir (Linux only; non-Linux platforms
//      don't always support directory fsync, so we swallow the error)
//   6. if running as root, best-effort chown to the `agent` user
//
// Concurrency:
//   Every public method tail-chains onto a `#chain` Promise so a second call
//   queues behind the first instead of racing on the file. Same trick
//   GitModule.#chain uses (see /workspace/packages/daemon/src/git/GitModule.ts).
//
// Validation:
//   Values containing `\n` would corrupt the dotenv-style line format. We
//   refuse to write such a value and throw `EnvVarValueRejected` — the
//   canonical file is left untouched (the .tmp may exist briefly until the
//   next successful rewrite overwrites it).

import { execFileSync } from 'node:child_process'
import * as nodeFs from 'node:fs/promises'
import path from 'node:path'
import type { Logger } from 'pino'

// SECURITY: never log values — keys-only.

export type EnvVarDelta = { key: string; value: string | null }
export type EnvVarEntry = { key: string; value: string }

/**
 * Thrown when a delta or initial entry contains a value with a newline. We
 * pin the offending key on the error so the caller / test can assert it
 * without needing to crack open the message.
 */
export class EnvVarValueRejected extends Error {
  readonly key: string
  constructor(args: { key: string }) {
    super(`env var value rejected: contains newline (key=${args.key})`)
    this.name = 'EnvVarValueRejected'
    this.key = args.key
  }
}

const FILE_MODE = 0o600
const DIR_MODE = 0o700
const TMP_SUFFIX = '.tmp'

/**
 * Subset of `node:fs/promises` we touch. Carved out as an interface so tests
 * can stub fragile bits (directory fsync on non-Linux, chown) without
 * monkey-patching the global module. Production wires the real `fs/promises`.
 */
export interface EnvVarManagerFs {
  mkdir: typeof nodeFs.mkdir
  writeFile: typeof nodeFs.writeFile
  rename: typeof nodeFs.rename
  open: typeof nodeFs.open
  chown: typeof nodeFs.chown
  unlink: typeof nodeFs.unlink
}

export interface EnvVarManagerDeps {
  envFilePath: string
  logger: Logger
  /** Test seam — defaults to `node:fs/promises`. */
  fs?: EnvVarManagerFs
  /**
   * Test seam — return the resolved {uid, gid} for the agent user, or null
   * if no such user exists. Production uses `id -u agent` / `id -g agent`.
   * Tests stub this to avoid touching the real user database.
   */
  resolveAgentUser?: () => { uid: number; gid: number } | null
  /**
   * Test seam — defaults to `process.getuid?.()`. Tests can pretend the
   * process is running as root or non-root without touching globals.
   */
  getuid?: () => number | undefined
}

export class EnvVarManager {
  readonly #envFilePath: string
  readonly #logger: Logger
  readonly #fs: EnvVarManagerFs
  readonly #resolveAgentUser: () => { uid: number; gid: number } | null
  readonly #getuid: () => number | undefined
  readonly #map: Map<string, string> = new Map()
  // Sequential dispatch — same shape as GitModule.#chain. A failed task does
  // NOT poison follow-ups; the failure is re-surfaced on the caller's promise
  // but the chain itself is reset to a resolved promise.
  #chain: Promise<unknown> = Promise.resolve()

  constructor(deps: EnvVarManagerDeps) {
    this.#envFilePath = deps.envFilePath
    this.#logger = deps.logger.child({ module: 'env-var-manager' })
    this.#fs = deps.fs ?? nodeFs
    this.#resolveAgentUser = deps.resolveAgentUser ?? defaultResolveAgentUser
    this.#getuid = deps.getuid ?? (() => process.getuid?.())
  }

  /**
   * Bootstrap-time replace. Clears the in-memory map and rewrites the file
   * with the given entries. Used by Card 8 (HTTP fetch-on-bootstrap) — Card 7
   * just exposes the shape.
   */
  async loadInitial(entries: ReadonlyArray<EnvVarEntry>): Promise<void> {
    return this.#enqueue(async () => {
      this.#map.clear()
      for (const e of entries) {
        rejectIfNewline(e.key, e.value)
        this.#map.set(e.key, e.value)
      }
      await this.#rewrite()
      // SECURITY: never log values — keys-only.
      this.#logger.info(
        { keysChanged: entries.map((e) => e.key), totalKeys: this.#map.size },
        'env vars loaded (initial)',
      )
    })
  }

  /**
   * Apply a delta. Set when `value` is a string, delete when it's `null`.
   * Empty deltas are a true no-op: no rewrite, no log churn.
   */
  async applyDelta(delta: ReadonlyArray<EnvVarDelta>): Promise<void> {
    return this.#enqueue(async () => {
      if (delta.length === 0) {
        return
      }
      // Validate first — we don't want to mutate the in-memory map and then
      // throw partway through, leaving the daemon in a state that diverges
      // from disk on the next successful rewrite.
      for (const d of delta) {
        if (d.value !== null) {
          rejectIfNewline(d.key, d.value)
        }
      }
      const keysChanged: string[] = []
      for (const d of delta) {
        keysChanged.push(d.key)
        if (d.value === null) {
          this.#map.delete(d.key)
        } else {
          this.#map.set(d.key, d.value)
        }
      }
      await this.#rewrite()
      // SECURITY: never log values — keys-only.
      this.#logger.info(
        { keysChanged, totalKeys: this.#map.size },
        'env vars updated (delta)',
      )
    })
  }

  /**
   * Snapshot of the current in-memory env map. Returned as a `ReadonlyMap`
   * view so callers can't mutate internal state. We hand back the live map
   * (cast to readonly) — every mutation goes through the chain serialiser
   * and we never expose write methods on the returned reference.
   */
  current(): ReadonlyMap<string, string> {
    return this.#map
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  #enqueue<T>(task: () => Promise<T>): Promise<T> {
    const next = this.#chain.then(task, task)
    // Swallow the rejection on the chain itself so a failed op doesn't poison
    // every subsequent op. Caller still sees the rejection via `next`.
    this.#chain = next.catch(() => undefined)
    return next
  }

  /**
   * Atomic rewrite of the snapshot file. Steps documented at top of file.
   */
  async #rewrite(): Promise<void> {
    const filePath = this.#envFilePath
    const tmpPath = filePath + TMP_SUFFIX
    const dir = path.dirname(filePath)

    // 1. Parent dir.
    await this.#fs.mkdir(dir, { recursive: true, mode: DIR_MODE })

    // 2. Build deterministic content (sorted by key for diffing/auditing).
    const content = renderContent(this.#map)

    // 3. Write tmp with secret-file mode.
    await this.#fs.writeFile(tmpPath, content, { mode: FILE_MODE })

    // 4. fsync the tmp file (durability before rename).
    try {
      const handle = await this.#fs.open(tmpPath, 'r+')
      try {
        await handle.sync()
      } finally {
        await handle.close()
      }
    } catch (err) {
      // fsync failures on the tmp file are unusual but recoverable: the
      // rename below will still succeed and the data is in the page cache.
      // Log and press on.
      this.#logger.warn({ err }, 'fsync on env tmp file failed (non-fatal)')
    }

    // 5. Atomic rename. If this throws, best-effort unlink the tmp file so
    //    we don't litter half-finished snapshots.
    try {
      await this.#fs.rename(tmpPath, filePath)
    } catch (err) {
      try {
        await this.#fs.unlink(tmpPath)
      } catch {
        // Tmp may not exist or unlink failed; the original error is more
        // interesting.
      }
      throw err
    }

    // 6. Best-effort fsync the parent directory. Linux supports this (it's
    //    how you guarantee the rename is durable across crashes); other
    //    platforms (macOS, Windows) often reject the operation. Swallow the
    //    error there — the rename itself is still correct.
    try {
      const dirHandle = await this.#fs.open(dir, 'r')
      try {
        await dirHandle.sync()
      } finally {
        await dirHandle.close()
      }
    } catch (err) {
      // Don't even warn on non-Linux — it's expected. On Linux, a real
      // failure is worth a debug breadcrumb but never escalates.
      this.#logger.debug({ err, platform: process.platform }, 'parent dir fsync skipped')
    }

    // 7. chown to agent user IFF running as root. Pure Docker prod path; in
    //    dev (non-root, no `agent` user), this is a no-op.
    if (this.#getuid() === 0) {
      const agent = this.#resolveAgentUser()
      if (agent === null) {
        this.#logger.warn(
          'agent user not found; leaving file ownership as current process',
        )
      } else {
        try {
          await this.#fs.chown(filePath, agent.uid, agent.gid)
        } catch (err) {
          this.#logger.warn({ err }, 'chown env file to agent failed (non-fatal)')
        }
      }
    }
  }
}

/**
 * Render the in-memory map as the dotenv-style snapshot. Sorted by key so the
 * file is deterministic across runs (helpful for diffing + audit).
 *
 * SECURITY: this function does NOT log; the returned string contains the
 * values verbatim. Callers must never log the return value.
 */
function renderContent(map: ReadonlyMap<string, string>): string {
  const keys = Array.from(map.keys()).sort()
  let out = ''
  for (const k of keys) {
    out += `${k}=${map.get(k) ?? ''}\n`
  }
  return out
}

function rejectIfNewline(key: string, value: string): void {
  if (value.includes('\n')) {
    throw new EnvVarValueRejected({ key })
  }
}

/**
 * Resolve the `agent` user's uid/gid via `id -u agent` / `id -g agent`. Used
 * only when the daemon runs as root (production Docker). Returns null if the
 * user doesn't exist — caller logs a warn and leaves ownership alone.
 *
 * Sync-call is fine: this only runs on rewrite-after-root-detection paths,
 * which are themselves async-serialised through `#chain`. No measurable cost
 * over `execFile`'s async dance and avoids one more layer of error plumbing.
 */
function defaultResolveAgentUser(): { uid: number; gid: number } | null {
  try {
    const uid = Number.parseInt(
      execFileSync('id', ['-u', 'agent'], { encoding: 'utf8' }).trim(),
      10,
    )
    const gid = Number.parseInt(
      execFileSync('id', ['-g', 'agent'], { encoding: 'utf8' }).trim(),
      10,
    )
    if (Number.isNaN(uid) || Number.isNaN(gid)) return null
    return { uid, gid }
  } catch {
    // `id` exits non-zero when the user doesn't exist (or it's not in PATH).
    // Either way, we can't chown safely — let the caller decide what to do.
    return null
  }
}
