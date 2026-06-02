import { isBenignAbortRejection } from './isBenignAbortRejection.js'

/**
 * Orphaned rejections/exceptions from external tooling (Cursor SDK shell,
 * child_process spawn, broken pipes to dead children) that must NOT take down
 * the daemon process. The SDK does not always attach `.catch()` to every
 * internal promise — a missing `/usr/bin/bash` or a dead shell child can
 * surface as `unhandledRejection` / `uncaughtException` at the Node top level.
 *
 * Swallowing these is conservative: we match errno + syscall and/or stack
 * frames that identify subprocess/shell tooling, not generic application bugs.
 */

function asErrno(err: Error): NodeJS.ErrnoException {
  return err as NodeJS.ErrnoException
}

/**
 * True when a rejection is a known subprocess/shell tooling failure that should
 * be logged and swallowed at the process boundary.
 */
export function isRecoverableToolingRejection(reason: unknown): boolean {
  if (reason === null || reason === undefined) return false
  const err =
    reason instanceof Error
      ? reason
      : typeof reason === 'object'
        ? Object.assign(new Error(String(reason)), reason)
        : new Error(String(reason))

  const { code, syscall, message, stack = '' } = asErrno(err)

  if (code === 'ENOENT') {
    if (syscall === 'spawn') return true
    if (/spawn\s+.+\s+ENOENT/i.test(message)) return true
    if (message.includes('spawn') && message.includes('ENOENT')) return true
  }

  if (code === 'EACCES' && syscall === 'spawn') return true

  if (code === 'EPIPE') {
    if (/BashState|LazyTerminalExecutor|child_process|node:child_process/i.test(stack)) {
      return true
    }
    if (syscall === 'write' && /BashState|LazyTerminalExecutor/i.test(stack)) {
      return true
    }
  }

  return false
}

/**
 * Combined gate for the top-level `unhandledRejection` / `uncaughtException`
 * handlers. Returns true for errors that should be logged (warn) but must not
 * invoke `process.exit(1)`.
 */
export function isNonFatalOrphanedError(reason: unknown): boolean {
  return isBenignAbortRejection(reason) || isRecoverableToolingRejection(reason)
}
