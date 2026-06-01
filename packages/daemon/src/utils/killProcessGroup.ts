// killProcessGroup — SIGTERM → SIGKILL escalation for detached child processes.
//
// Both HookExecutor and GitRunner spawn their children with `detached: true`,
// which puts the child into its own process group. That lets us send a signal
// to the *whole group* via `process.kill(-pid, sig)` — reaching not just the
// shell or the git binary itself but every grandchild it spawned (e.g. `sleep`
// under `npm run …`, or `ssh`/`ssh-askpass` under `git fetch`). Without group
// kill, those grandchildren keep stdio pipes open and the 'close' event never
// fires within the timeout window.
//
// Spec 13 Card 10 requires a two-phase escalation: SIGTERM first, then SIGKILL
// after a configurable grace period if the child is still alive. This helper
// is the single source of truth for that sequence — used by both the hook
// timeout/abort paths and the git timeout/abort paths (4 callsites total),
// which is why it lives here rather than as a private function in either
// module.

import type { Logger } from 'pino'

export interface KillProcessGroupOpts {
  /**
   * The leader pid of the process group. Will be sent the signal as `-pid` so
   * the *group* receives it, not just the lone process. Spawn must have used
   * `detached: true` for this to be a real group; otherwise the negative pid
   * is meaningless. If the spawn never landed a pid (undefined), the helper
   * is a no-op — there's nothing to signal.
   */
  pid: number | undefined
  /**
   * Resolves when the child process emits 'close'. We race against the
   * escalation timer: if this resolves first, we cancel the SIGKILL and
   * never escalate. If the timer fires first, we send SIGKILL and log a
   * warning so operators see the wedged-process pattern in production logs.
   */
  processClosed: Promise<void>
  /** ms between SIGTERM dispatch and SIGKILL escalation. */
  escalationMs: number
  logger: Logger
  /** Free-form tag, included in the warn log so the operator knows why we killed. */
  reason: string
}

/**
 * Send SIGTERM to the process group leader's group, then escalate to SIGKILL
 * after `escalationMs` if `processClosed` hasn't resolved yet. Fire-and-forget
 * — the caller doesn't need to await; the internal Promise.race handles
 * cleanup. Both signal sends suppress ESRCH (process already gone) and similar
 * races; the fallback path tries a single-process kill if the group-kill
 * raises (typically meaning the leader has already exited and the kernel
 * tore the group down).
 */
export function killProcessGroupWithEscalation(opts: KillProcessGroupOpts): void {
  const { pid, processClosed, escalationMs, logger, reason } = opts

  if (pid === undefined) {
    // Spawn never produced a pid — nothing to signal. This matches the
    // existing tryKill behaviour in both modules.
    return
  }

  // Phase 1: SIGTERM the whole process group.
  trySignal(pid, 'SIGTERM')

  // Phase 2: schedule SIGKILL escalation. Cancel if the child closes first.
  const timer = setTimeout(() => {
    logger.warn(
      { pid, reason, escalationMs },
      'sigterm timeout — escalating to sigkill',
    )
    trySignal(pid, 'SIGKILL')
  }, escalationMs)
  // unref so a still-pending timer never blocks the daemon's event-loop drain.
  // The `?.` guards against the test seam where setTimeout returns a number
  // (legacy node-types contexts) instead of a Timeout object.
  timer.unref?.()

  // When the process actually closes, drop the pending escalation timer so
  // we don't fire SIGKILL on an already-dead pid (which would be a benign
  // ESRCH, but the warn log would be misleading).
  processClosed
    .then(() => {
      clearTimeout(timer)
    })
    .catch(() => {
      // processClosed should never reject in the callers we have, but be
      // defensive — if it does, we still want to clear the timer to avoid
      // a spurious escalation log. Best-effort.
      clearTimeout(timer)
    })
}

/**
 * Best-effort signal to a detached child's whole process group. Suppresses
 * ESRCH (process gone) and falls back to single-process kill via `process.kill`
 * with the positive pid if the group-kill raises — which happens when the
 * group leader has already exited and the kernel tore the group down before
 * we got here.
 *
 * The negative-pid signal IS the documented Unix semantics for "send to
 * process group N" (see kill(2)). TypeScript's typings don't model this, so
 * the call site is annotated.
 */
function trySignal(pid: number, sig: NodeJS.Signals): void {
  try {
    // process.kill(-pid, sig) — group kill. Documented Unix semantics; not
    // typed in @types/node so the negative pid is opaque to TS, but that's
    // fine here.
    process.kill(-pid, sig)
    return
  } catch {
    // Typically ESRCH — the group leader is already gone, or never existed
    // (spawn raced and the child died before we got here). Try the
    // single-process path as fallback.
  }
  try {
    process.kill(pid, sig)
  } catch {
    // Process already exited or unkillable; nothing useful we can do.
  }
}
