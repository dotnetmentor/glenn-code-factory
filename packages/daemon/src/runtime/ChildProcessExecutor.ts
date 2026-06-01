// ChildProcessExecutor — production impl of {@link IExecutor}. Wraps
// `node:child_process.execFile` with promise semantics and a default 60s
// timeout. Used by SupervisordController and other runtime shell-outs.
//
// === Why execFile + promisify and not spawn? ===
//
// The runtime shell-outs (`supervisorctl reread`, `supervisorctl update`,
// the bash `install` script, etc.) are short, non-interactive commands that produce
// modest output. We don't need to stream stdout incrementally and we don't
// need to wire to a tty — `execFile` is the simplest primitive that gives us
// both stdout/stderr capture and a wait-for-exit promise in two lines. If a
// future card needs streaming progress (per-line stdout from a long-running
// rust install) we can switch this single module without touching its callers.
//
// === Errors ===
//
// On non-zero exit, `execFile` rejects with an Error decorated with `code`
// (the exit code) and `stderr`/`stdout` strings. We unwrap that into a
// regular Error with a stable message shape so tests have something they can
// assert against without leaning on the node-internal Error subtype.
//
// When `opts.allowNonZero` is true, we catch the rejection and resolve with
// the captured streams + exit code instead. Used by callers that want to
// probe a tool's behaviour without throwing on failure.

import { execFile, spawn } from 'node:child_process'
import { promisify } from 'node:util'

import type { ExecOpts, ExecResult, IExecutor } from './IExecutor.js'

const execFileAsync = promisify(execFile)

const DEFAULT_TIMEOUT_MS = 60_000

/**
 * Subset of the Node-decorated Error that `execFile` rejects with on non-zero
 * exit. We pick fields off this shape rather than `instanceof`-ing the
 * (private) `ExecFileException` class because that class isn't exported
 * from `node:child_process`.
 */
type ExecFileError = Error & {
  code?: number | string | null
  signal?: NodeJS.Signals | null
  stdout?: string | Buffer
  stderr?: string | Buffer
}

function isExecFileError(err: unknown): err is ExecFileError {
  return err instanceof Error && ('code' in err || 'stderr' in err || 'stdout' in err)
}

function asString(v: string | Buffer | undefined): string {
  if (v === undefined) return ''
  if (typeof v === 'string') return v
  return v.toString('utf8')
}

export class ChildProcessExecutor implements IExecutor {
  async run(
    command: string,
    args: readonly string[],
    opts: ExecOpts = {},
  ): Promise<ExecResult> {
    const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS

    // When the caller wants streaming stdout/stderr, fall back to spawn so we
    // can wire `data` listeners. Otherwise stick with execFile — same memory
    // bounds + simpler error path. No behavioural change for existing callers.
    if (opts.onStdout !== undefined || opts.onStderr !== undefined) {
      return await this.#runWithStreaming(command, args, opts, timeoutMs)
    }

    try {
      const execOpts: Parameters<typeof execFileAsync>[2] & object = {
        timeout: timeoutMs,
        // Cap captured output at 8MB — longer than any reasonable mise/
        // supervisorctl output but small enough to bound memory if a process
        // goes haywire and floods stdout.
        maxBuffer: 8 * 1024 * 1024,
        // We capture as utf8 strings; binary stdout would be unusual for the
        // commands this executor is used with.
        encoding: 'utf8',
      }
      if (opts.cwd !== undefined) execOpts.cwd = opts.cwd
      if (opts.env !== undefined) {
        // execFile expects undefined-free env; strip undefined entries.
        execOpts.env = Object.fromEntries(
          Object.entries(opts.env).filter(([, v]) => v !== undefined),
        ) as NodeJS.ProcessEnv
      }
      const { stdout, stderr } = await execFileAsync(command, [...args], execOpts)
      return {
        stdout: asString(stdout),
        stderr: asString(stderr),
        exitCode: 0,
      }
    } catch (err) {
      // execFile rejects with a decorated Error on non-zero exit, signal, or
      // timeout. Differentiate via `code` and `signal`.
      if (!isExecFileError(err)) {
        throw err
      }
      const stdout = asString(err.stdout)
      const stderr = asString(err.stderr)
      const exitCode = typeof err.code === 'number' ? err.code : -1

      // Timeout / signal kill — surface as a regular Error with a stable
      // shape regardless of `allowNonZero`. A timeout is a programming
      // problem (the timeout was too low) or a hung child; the caller almost
      // never wants to silently swallow it.
      if (err.signal) {
        throw new Error(
          `${command} killed by signal ${err.signal} (timeoutMs=${timeoutMs})`,
        )
      }

      if (opts.allowNonZero === true) {
        return { stdout, stderr, exitCode }
      }

      const tail = stderr.trim() || stdout.trim() || err.message
      throw new Error(`${command} failed (exit ${exitCode}): ${tail}`)
    }
  }

  /**
   * Streaming variant of {@link run} for callers that need live stdout/stderr
   * chunks (mise install, git clone, npm install, …). Captures the same final
   * result shape as the non-streaming path so callers don't branch on which
   * one ran.
   */
  async #runWithStreaming(
    command: string,
    args: readonly string[],
    opts: ExecOpts,
    timeoutMs: number,
  ): Promise<ExecResult> {
    return await new Promise<ExecResult>((resolve, reject) => {
      const spawnOpts: Parameters<typeof spawn>[2] & object = {}
      if (opts.cwd !== undefined) spawnOpts.cwd = opts.cwd
      if (opts.env !== undefined) {
        spawnOpts.env = Object.fromEntries(
          Object.entries(opts.env).filter(([, v]) => v !== undefined),
        ) as NodeJS.ProcessEnv
      }

      const child = spawn(command, [...args], spawnOpts)

      let stdout = ''
      let stderr = ''
      const stdoutCb = opts.onStdout
      const stderrCb = opts.onStderr

      child.stdout?.setEncoding('utf8')
      child.stderr?.setEncoding('utf8')
      child.stdout?.on('data', (chunk: string) => {
        stdout += chunk
        if (stdoutCb !== undefined) {
          try {
            stdoutCb(chunk)
          } catch {
            // Swallow — a misbehaving callback must not crash the spawn.
          }
        }
      })
      child.stderr?.on('data', (chunk: string) => {
        stderr += chunk
        if (stderrCb !== undefined) {
          try {
            stderrCb(chunk)
          } catch {
            // Swallow — same reason as above.
          }
        }
      })

      const timer = setTimeout(() => {
        child.kill('SIGKILL')
      }, timeoutMs)

      child.on('error', (err) => {
        clearTimeout(timer)
        reject(err)
      })
      child.on('close', (code, signal) => {
        clearTimeout(timer)
        const exitCode = typeof code === 'number' ? code : -1
        if (signal !== null) {
          reject(
            new Error(`${command} killed by signal ${signal} (timeoutMs=${timeoutMs})`),
          )
          return
        }
        if (exitCode === 0 || opts.allowNonZero === true) {
          resolve({ stdout, stderr, exitCode })
          return
        }
        const tail = stderr.trim() || stdout.trim() || `exit ${exitCode}`
        reject(new Error(`${command} failed (exit ${exitCode}): ${tail}`))
      })
    })
  }
}
