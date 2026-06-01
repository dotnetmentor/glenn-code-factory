// InstallHashStore — single source of truth for the daemon's install-script
// hash cache.
//
// === Why ===
//
// Runtime Spec V2 ships a `install` bash field at the top level and per-service.
// Re-running these scripts on every bootstrap would be slow (apt installs,
// binary downloads, mise toolchain compiles). Per the spec's Scenario 5 / Card
// P2: hash the install script(s); re-run only when the hash differs from the
// last-applied one. The store records last-applied hashes per scope:
//
//   - `topLevel`: SHA-256 of `spec.install` ?? ''
//   - `services[name]`: SHA-256 of `service.install` ?? '' for each service
//
// === Why one file (not /data/.glenn/install-hash) ===
//
// The card description originally proposed a single `install-hash` file with
// just the blob hash. Live-mutation handling (Card P2.2) needs to know which
// individual services have changed install scripts so it can re-run just
// those, not the whole blob. Storing per-service hashes from day one means
// P2.2 can read this file without us reshuffling the on-disk layout later.
//
// File: `/data/.glenn/install-hashes.json`. JSON object:
//   { "topLevel": "sha256-hex", "services": { "<name>": "sha256-hex", ... } }
//
// === Atomic writes ===
//
// Same dance as WritingConfigStage / EnvVarManager: write to `<path>.tmp`,
// then `rename` into place. A crash mid-write leaves the original file
// untouched (or absent on first boot). The reader tolerates a missing or
// malformed file by treating it as "no cached hashes" — everything reinstalls.
//
// === Blob composition ===
//
// `composeBlob(spec)` is the canonical "what gets hashed + executed for the
// top-level install pipeline" reducer. Order matters because the same chunks
// in a different order would produce a different SHA — that would invalidate
// the cache on every boot for no real reason.
//
//   1. spec.install (if non-empty)
//   2. each service.install in services[] order (if non-empty)
//
// Each chunk gets a trailing `\n` so concatenated bash doesn't accidentally
// glue the last line of one chunk onto the first line of the next.

import { createHash } from 'node:crypto'
import { rename, writeFile } from 'node:fs/promises'
import { readFile } from 'node:fs/promises'

import type { RuntimeSpecV2 } from '../signalr/types.js'

const DEFAULT_PATH = '/data/.glenn/install-hashes.json'

export interface InstallHashes {
  /** SHA-256 of `spec.install` (empty string if absent). */
  topLevel: string
  /** Per-service SHA-256, keyed by service name. */
  services: Record<string, string>
}

export interface InstallHashStoreDeps {
  /** Override the on-disk path for tests. */
  path?: string
  /** Hand-rolled fs surface so tests don't touch real disk. */
  fs?: InstallHashStoreFs
}

/** Subset of `node:fs/promises` the store reaches for. */
export interface InstallHashStoreFs {
  readFile: typeof readFile
  writeFile: typeof writeFile
  rename: typeof rename
}

const EMPTY_HASHES: InstallHashes = Object.freeze({
  topLevel: '',
  services: Object.freeze({}) as Record<string, string>,
}) as InstallHashes

/**
 * SHA-256 of a string, returned hex-encoded. Stable across Node versions.
 *
 * Hashing the empty string is well-defined and produces a known constant —
 * we return that rather than special-casing because the caller will compare
 * "no install" to "no install" and a match is correct (nothing to do).
 */
export function sha256Hex(input: string): string {
  return createHash('sha256').update(input, 'utf8').digest('hex')
}

/**
 * Compose the install bash blob from a V2 runtime spec. Top-level first, then
 * services in spec-array order. Each non-empty chunk gets a trailing newline.
 * Empty/missing chunks are skipped entirely (no leading blank lines).
 *
 * The return value is exactly what InstallStage will hand to `bash -c`, so
 * the hash of this string is also the hash of "the script we'd run".
 */
export function composeBlob(spec: RuntimeSpecV2): string {
  const parts: string[] = []
  const top = (spec.install ?? '').trim()
  if (top.length > 0) {
    parts.push(top.endsWith('\n') ? top : `${top}\n`)
  }
  for (const svc of spec.services ?? []) {
    const chunk = (svc.install ?? '').trim()
    if (chunk.length === 0) continue
    parts.push(chunk.endsWith('\n') ? chunk : `${chunk}\n`)
  }
  return parts.join('')
}

/**
 * Compute the per-scope hash map for a V2 spec. The `topLevel` field is
 * `sha256(spec.install ?? '')` (NOT the blob); each `services[name]` entry is
 * the SHA of that single service's `install` field. Live-mutation will use
 * these to decide which subset to re-run.
 */
export function computeSpecHashes(spec: RuntimeSpecV2): InstallHashes {
  const services: Record<string, string> = {}
  for (const svc of spec.services ?? []) {
    services[svc.name] = sha256Hex(svc.install ?? '')
  }
  return {
    topLevel: sha256Hex(spec.install ?? ''),
    services,
  }
}

/**
 * Persistent per-scope hash cache. One instance is enough for the daemon's
 * lifetime — there's no in-memory state, every method reads/writes the file
 * directly so writes are immediately visible to any future reader (including
 * Card P2.2's live-delta path).
 */
export class InstallHashStore {
  readonly #path: string
  readonly #fs: InstallHashStoreFs

  constructor(deps: InstallHashStoreDeps = {}) {
    this.#path = deps.path ?? DEFAULT_PATH
    this.#fs = deps.fs ?? {
      readFile,
      writeFile,
      rename,
    }
  }

  /** Absolute path the store reads/writes. Exposed for diagnostics + tests. */
  get path(): string {
    return this.#path
  }

  /**
   * Read the cached hashes. Returns the EMPTY sentinel when:
   *   - the file does not exist (first boot on a fresh volume)
   *   - the file is malformed JSON
   *   - the JSON shape doesn't match `InstallHashes` (wrong key types)
   *
   * Treating all three the same way means InstallStage just runs the install
   * blob fresh — same as a hash mismatch — which is the safe default.
   */
  async read(): Promise<InstallHashes> {
    let raw: string
    try {
      raw = await this.#fs.readFile(this.#path, 'utf8')
    } catch (err) {
      // ENOENT (no file yet) is the common case on first boot. Any other
      // read error (EACCES, EIO, ...) is suspicious but the safe response
      // is still "treat as empty + re-run install"; the file gets rewritten
      // on a successful run.
      void err
      return cloneEmpty()
    }
    let parsed: unknown
    try {
      parsed = JSON.parse(raw)
    } catch {
      return cloneEmpty()
    }
    if (!isInstallHashes(parsed)) {
      return cloneEmpty()
    }
    return parsed
  }

  /**
   * Persist a new hash snapshot atomically. Writes to `<path>.tmp` first,
   * then renames into place — a crash mid-write never leaves a half-written
   * file at the canonical path.
   */
  async write(hashes: InstallHashes): Promise<void> {
    const tmp = `${this.#path}.tmp`
    const body = JSON.stringify(
      {
        topLevel: hashes.topLevel,
        services: { ...hashes.services },
      },
      null,
      2,
    ) + '\n'
    await this.#fs.writeFile(tmp, body, 'utf8')
    await this.#fs.rename(tmp, this.#path)
  }
}

/**
 * Validate that a parsed JSON value matches `InstallHashes`. Defensive against
 * an externally-written or older-version file ending up at this path.
 */
function isInstallHashes(value: unknown): value is InstallHashes {
  if (value === null || typeof value !== 'object') return false
  const v = value as { topLevel?: unknown; services?: unknown }
  if (typeof v.topLevel !== 'string') return false
  if (v.services === null || typeof v.services !== 'object') return false
  for (const entry of Object.values(v.services as Record<string, unknown>)) {
    if (typeof entry !== 'string') return false
  }
  return true
}

function cloneEmpty(): InstallHashes {
  return { topLevel: EMPTY_HASHES.topLevel, services: {} }
}
