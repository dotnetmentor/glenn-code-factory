// IExecutor — the shell-out test seam used by the runtime-curation modules
// (Spec 16 Card 6). Production binds to {@link ChildProcessExecutor}; tests
// hand-roll a fake so they never spawn real processes.
//
// We deliberately keep the surface tiny: one `run()` that takes argv and
// resolves with stdout/stderr/exitCode. Everything else (timeouts, abort,
// streaming) is layered on the same primitive — callers don't need an
// `execAsync`/`spawnInteractive` split for the work this card does (mise +
// supervisorctl are short-lived, non-interactive).

export type ExecResult = {
  readonly stdout: string
  readonly stderr: string
  readonly exitCode: number
}

export type ExecOpts = {
  /**
   * When true, a non-zero exit code resolves with the result instead of
   * throwing. Used for cases where the caller wants to inspect stderr/stdout
   * before deciding what to do (e.g. probing whether a tool is installed).
   * Default: false — non-zero throws.
   */
  allowNonZero?: boolean
  /**
   * Wall-clock timeout before the child is killed and the call rejects.
   * Default chosen at the implementation layer (60s in
   * {@link ChildProcessExecutor}).
   */
  timeoutMs?: number
  /**
   * Optional working directory for the spawned child. Production paths use
   * absolute paths; relative paths resolve against the daemon's cwd.
   */
  cwd?: string
  /**
   * Optional environment merged on top of `process.env` for the spawned child.
   * Used by `RunningSetupStage` to set a fixed `PATH` that includes
   * `/data/mise/shims` and by `CloningRepoStage` to set `GIT_SSH_COMMAND`.
   */
  env?: Readonly<Record<string, string | undefined>>
  /**
   * Stream-oriented stdout chunks. The implementation invokes this with each
   * chunk as it arrives — used by long-running commands (mise install, npm
   * install, git clone) to surface live progress. Optional; when absent, the
   * call still resolves with the final captured stdout in `ExecResult`.
   */
  onStdout?: (chunk: string) => void
  /**
   * Stream-oriented stderr chunks. Same shape as `onStdout`.
   */
  onStderr?: (chunk: string) => void
}

export interface IExecutor {
  /**
   * Run a command and wait for completion. Throws on non-zero exit unless
   * `allowNonZero` is true. The implementation is opinion-free about the
   * binary path — pass an absolute path or a name resolvable on PATH; we
   * delegate to the impl.
   */
  run(command: string, args: readonly string[], opts?: ExecOpts): Promise<ExecResult>
}
