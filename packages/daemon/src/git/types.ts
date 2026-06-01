// Wire types for the git ops primitive (Card 5).
//
// PascalCase enum mirror — the .NET server uses `JsonStringEnumConverter` with
// no naming policy, so the wire shape matches the C# enum member names verbatim.
// Source of truth for the enum is `Source/Features/GitOps/Models/GitOperation.cs`
// (`GitOpType`); update both sides together.
//
// `GitInvocation` and `GitResult` are the runner's input/output. `GitAuditEvent`
// is what the runner emits via `onAudit` so the next layer (GitModule, Card 7)
// can persist + ship over SignalR without re-deriving the same fields.

export type GitOpType =
  | 'Clone'
  | 'Checkout'
  | 'Add'
  | 'Commit'
  | 'Push'
  | 'Fetch'
  | 'Merge'
  | 'BranchCreate'
  | 'BranchList'
  | 'Reset'
  | 'ForcePush'
  | 'BranchDelete'

export interface GitInvocation {
  /** Logical operation kind — drives audit + destructive-op classification (Card 9). */
  op: GitOpType
  /** Argv passed to /usr/bin/git verbatim. The runner does NOT inspect or mutate. */
  args: string[]
  /**
   * When true, the audit `commandLine` is recorded as `git <op> [redacted]`
   * (instead of the full argv) — used for ops where args carry sensitive data
   * (URLs with embedded creds, etc).
   */
  sensitive?: boolean
  /** Per-op timeout. Default 60s, hard-capped at 5m. */
  timeoutMs?: number
}

export interface GitResult {
  /** null if the child was killed (timeout or external abort). */
  exitCode: number | null
  durationMs: number
  /**
   * Last ~16 KiB of the last 100 lines of combined stdout+stderr, '\n'-joined.
   * Display-only — use `outputHash` for stream identity.
   */
  outputTail: string
  /** sha256 hex (lower-case, 64 chars) over the full combined byte stream. */
  outputHash: string
  timedOut: boolean
  /**
   * Heuristic on the captured output for "publickey denied / auth failed /
   * could not read from remote". Consumed by GitModule to surface a recover
   * action without parsing exit codes.
   */
  authError: boolean
}

export interface GitAuditEvent {
  kind: 'started' | 'completed'
  /** Stable across started/completed for the same invocation. */
  executionId: string
  op: GitOpType
  /** `git <args>` verbatim, or `git <op> [redacted]` when `sensitive: true`. */
  commandLine: string
  startedAt: Date

  // completed-only — all undefined on `started` events.
  endedAt?: Date
  exitCode?: number | null
  durationMs?: number
  outputTail?: string
  outputHash?: string
  timedOut?: boolean
  authError?: boolean
}
