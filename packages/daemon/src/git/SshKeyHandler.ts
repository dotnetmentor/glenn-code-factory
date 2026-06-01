// SshKeyHandler — Card 6 of daemon-git-ops.
//
// Installs / rotates the SSH deploy key the GitRunner uses for `github.com`
// auth. Pure I/O wrapper around `<homeDir>/.ssh/`:
//
//   - id_ed25519       : the private key body, mode 0600
//   - config           : a three-line ssh_config snippet pinning IdentityFile
//                        + StrictHostKeyChecking=accept-new for github.com
//
// Behaviour (mirrors the GitRunner's expectations and the card brief):
//
//   - `deployKey == null` is "leave existing alone", NOT "delete". Lets the
//     daemon survive a config push that happens to omit the key (e.g. an
//     unrelated update) without bricking the next git op.
//
//   - Idempotency: read-and-compare before write. Avoids spurious mtime churn
//     and gives us a clean "no-op" debug log so operators can tell rotations
//     from steady-state config refreshes.
//
//   - Atomic write: write to `<final>.tmp` then rename. A crash mid-write
//     can never leave a half-written private key on disk. We chmod the final
//     path again post-rename as belt-and-braces (the temp file already had
//     the right mode, but EXT-attrs-on-rename behaviour varies by FS).
//
//   - chmod failure on the post-rename safety call is logged and swallowed —
//     the key is on disk with the right mode from the temp-file write, so
//     erroring out here would be worse than the warning. Throwing would
//     leave the daemon unable to use a perfectly good key.
//
//   - The PEM body is NEVER logged. We log presence (`'deploy key installed'`)
//     and idempotency outcomes, never bytes.
//
// The handler is light on dependencies (just fs + path + os) and exposes a
// single `applyConfig` so Card 10 can wire it from the UpdateConfig handler
// alongside hooksJson / runtimeToken without touching internals.

import * as nodeFs from 'node:fs/promises'
import { homedir as nodeHomedir } from 'node:os'
import path from 'node:path'
import type { Logger } from 'pino'

/**
 * Subset of `node:fs/promises` we need. Carved out as an interface so tests
 * can inject a partial fake (specifically: rejecting `chmod` once to exercise
 * the swallow-and-warn path) without monkey-patching the global module.
 */
export interface SshKeyHandlerFs {
  mkdir: typeof nodeFs.mkdir
  readFile: typeof nodeFs.readFile
  writeFile: typeof nodeFs.writeFile
  rename: typeof nodeFs.rename
  chmod: typeof nodeFs.chmod
  unlink: typeof nodeFs.unlink
}

export interface SshKeyHandlerOpts {
  /** Injectable for tests; defaults to `os.homedir()`. */
  homeDir?: string
  logger: Logger
  /** Test seam — inject a partial fs mock. Defaults to `node:fs/promises`. */
  fs?: SshKeyHandlerFs
}

export interface SshConfig {
  /** PEM body. `null`/`undefined` means "leave existing key alone". */
  deployKey?: string | null
}

const KEY_FILENAME = 'id_ed25519'
const TMP_SUFFIX = '.tmp'
const CONFIG_FILENAME = 'config'
const SSH_DIR = '.ssh'

const KEY_MODE = 0o600
const SSH_DIR_MODE = 0o700

export class SshKeyHandler {
  readonly #homeDir: string
  readonly #logger: Logger
  readonly #fs: SshKeyHandlerFs

  constructor(opts: SshKeyHandlerOpts) {
    this.#homeDir = opts.homeDir ?? nodeHomedir()
    this.#logger = opts.logger.child({ module: 'ssh-key-handler' })
    this.#fs = opts.fs ?? nodeFs
  }

  async applyConfig(config: SshConfig): Promise<void> {
    const deployKey = config.deployKey
    if (deployKey === null || deployKey === undefined) {
      this.#logger.debug('no deploy key in config; leaving existing key alone')
      return
    }

    const sshDir = path.join(this.#homeDir, SSH_DIR)
    const keyPath = path.join(sshDir, KEY_FILENAME)
    const tmpPath = keyPath + TMP_SUFFIX
    const configPath = path.join(sshDir, CONFIG_FILENAME)

    // Ensure ~/.ssh exists with mode 0700. mkdir recursive is a no-op if it
    // already exists; we re-chmod to lock down a pre-existing world-readable
    // dir on first install.
    await this.#fs.mkdir(sshDir, { recursive: true, mode: SSH_DIR_MODE })
    try {
      await this.#fs.chmod(sshDir, SSH_DIR_MODE)
    } catch (err) {
      this.#logger.warn({ err }, 'chmod on .ssh dir failed (non-fatal)')
    }

    // Idempotency check on the key body itself. ENOENT → first install.
    let existingKey: string | null = null
    try {
      existingKey = await this.#fs.readFile(keyPath, 'utf8')
    } catch (err) {
      if (!isENOENT(err)) throw err
    }

    if (existingKey === deployKey) {
      this.#logger.debug('deploy key unchanged; no-op')
    } else {
      // Atomic write: tmp file with target mode, then rename. Belt-and-braces
      // chmod after rename in case the FS dropped the mode on the rename.
      await this.#fs.writeFile(tmpPath, deployKey, { mode: KEY_MODE })
      try {
        await this.#fs.rename(tmpPath, keyPath)
      } catch (err) {
        // Rename failed — best-effort cleanup of the tmp file so we don't
        // litter a half-finished private key on disk. Re-throw the original.
        try {
          await this.#fs.unlink(tmpPath)
        } catch {
          // Tmp may not exist, or unlink itself failed. Either way, we're
          // about to throw the more interesting error.
        }
        throw err
      }
      try {
        await this.#fs.chmod(keyPath, KEY_MODE)
      } catch (err) {
        // The key is on disk with the correct mode from the temp-file write.
        // chmod here is a defence-in-depth pass; failure is non-fatal.
        this.#logger.warn({ err }, 'post-rename chmod on deploy key failed (non-fatal)')
      }
      // Note: we deliberately do NOT include the key body in this log line.
      this.#logger.info('deploy key installed')
    }

    // ssh_config snippet. Idempotent: read-and-compare. Mode is left at the
    // process default — it's not sensitive.
    const expectedConfig = renderSshConfig(this.#homeDir)
    let existingConfig: string | null = null
    try {
      existingConfig = await this.#fs.readFile(configPath, 'utf8')
    } catch (err) {
      if (!isENOENT(err)) throw err
    }
    if (existingConfig !== expectedConfig) {
      await this.#fs.writeFile(configPath, expectedConfig)
    }
  }
}

/**
 * Render the three-line ssh_config snippet with the resolved homeDir baked
 * into the IdentityFile path. We don't use `~` because git's GIT_SSH_COMMAND
 * runs in a non-interactive shell that won't always tilde-expand, and we want
 * the same content to work in tempdir-based tests without further plumbing.
 */
function renderSshConfig(homeDir: string): string {
  const keyPath = path.join(homeDir, SSH_DIR, KEY_FILENAME)
  return `Host github.com\n  IdentityFile ${keyPath}\n  StrictHostKeyChecking accept-new\n`
}

function isENOENT(err: unknown): boolean {
  return (
    typeof err === 'object' &&
    err !== null &&
    'code' in err &&
    (err as { code?: unknown }).code === 'ENOENT'
  )
}
