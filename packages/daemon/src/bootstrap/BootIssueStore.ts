// BootIssueStore — in-memory record of non-fatal bootstrap problems.
//
// === Spec context (self-healing-runtime-specs, card D1 + D2) ===
//
// The degraded-online bootstrap decouples "runtime is alive/Online" from "the
// runtime spec fully applied". When a NON-CRITICAL (spec) stage — Install,
// RunningSetup, StartingServices — fails deterministically, the orchestrator
// does NOT abort the boot. Instead it records a `BootIssue` here and marches on
// so `ReportReadyStage` still runs and the runtime reaches Online in a
// `Degraded` health state.
//
// This store is the single in-memory home for those issues. It is:
//
//   - Injected into the orchestrator (so the orchestrator can append).
//   - Read by D2's `get_boot_issues` MCP tool (so the embedded agent can read
//     exactly what failed and propose a corrected spec). That's why this is a
//     standalone injectable class rather than a private orchestrator field — a
//     later card needs a stable handle to the same instance the orchestrator
//     wrote into.
//
// Lifetime: one instance per daemon process, constructed in the composition
// root and shared between the orchestrator and the MCP tool surface. It is NOT
// persisted — the durable copy of each issue lives in `RuntimeEvents`
// (SpecDegraded events the orchestrator emits per issue) + `RuntimeErrorReports`
// on the backend. This store is the live, low-latency view for the in-process
// agent loop.

/**
 * One non-fatal bootstrap problem. Shape is the contract D2's `get_boot_issues`
 * tool serialises to the agent — keep it stable + JSON-friendly (all primitive
 * fields, no class instances).
 */
export interface BootIssue {
  /** The bootstrap stage that produced the issue (e.g. `'install'`, `'starting-services'`). */
  stage: string
  /**
   * The specific service this issue concerns, when the stage is service-scoped
   * (StartingServicesStage records one issue per failed/wedged service). Absent
   * for stage-wide failures (install bash failed, setup bash failed).
   */
  service?: string
  /** Short human-readable summary of what went wrong (one line). */
  reason: string
  /**
   * Optional richer context — a log tail, the failing command's stderr, the
   * supervisord state, etc. Free-form; the agent reads this to diagnose.
   */
  detail?: string
  /** ISO8601 timestamp the issue was recorded. */
  occurredAt: string
}

/**
 * Thin in-memory collector for {@link BootIssue}s. Append-only during a boot;
 * `list()` returns a defensive copy so callers (the MCP tool) can't mutate the
 * backing array.
 */
export class BootIssueStore {
  readonly #issues: BootIssue[] = []

  /**
   * Record one non-fatal boot issue. `occurredAt` is stamped here (caller need
   * not supply it) unless the caller passes an explicit value — handy for
   * deterministic tests.
   */
  record(issue: Omit<BootIssue, 'occurredAt'> & { occurredAt?: string }): void {
    this.#issues.push({
      stage: issue.stage,
      ...(issue.service !== undefined ? { service: issue.service } : {}),
      reason: issue.reason,
      ...(issue.detail !== undefined ? { detail: issue.detail } : {}),
      occurredAt: issue.occurredAt ?? new Date().toISOString(),
    })
  }

  /** Defensive snapshot of every recorded issue, in record order. */
  list(): BootIssue[] {
    return this.#issues.map((i) => ({ ...i }))
  }

  /** Number of issues recorded so far. */
  get count(): number {
    return this.#issues.length
  }

  /** True when at least one issue has been recorded (→ `Degraded`). */
  hasIssues(): boolean {
    return this.#issues.length > 0
  }
}
