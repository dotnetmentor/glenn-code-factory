using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Central record tracking a project's runtime — the Box VM (box.ascii.dev) it
/// runs on, and where it currently sits in the lifecycle state graph
/// (<see cref="RuntimeState"/>).
///
/// <list type="bullet">
///   <item>One row per project. Created when a project first asks for a runtime.</item>
///   <item><see cref="ProjectId"/> is a plain Guid — no FK — because the Project
///         entity belongs to a future spec. This mirrors the
///         <c>BoxOperation.RuntimeId</c> and <c>BootstrapRun.RuntimeId</c>
///         convention.</item>
///   <item>Soft-deletable: <c>Deleted</c> state has a 30-day window before the
///         janitor hard-deletes the row; the Box behind it is deleted at
///         Deleting time (a stopped box costs ~nothing, but a deleted project
///         must not leave billable snapshots behind).</item>
/// </list>
///
/// <para>This card is intentionally <i>data only</i>. The state-machine
/// transition methods, domain events and behaviour all live in follow-up
/// cards. The base class is still <see cref="Entity"/> so future cards can
/// raise events from instance methods without a model change.</para>
/// </summary>
public class ProjectRuntime : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The project this runtime belongs to. FK to <see cref="Project"/> —
    /// promoted from a plain Guid in the e2e-smoketest spec, now that the
    /// Project entity lives in <c>Source.Features.Projects</c>. Indexed for the
    /// dominant "show me the runtime for project X" lookup.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>Navigation to the owning project.</summary>
    public Project Project { get; set; } = null!;

    /// <summary>
    /// The branch this runtime is pinned to. FK to <see cref="ProjectBranch"/>.
    /// A runtime is pinned to one <c>(Project, Branch)</c> pair for life — there
    /// is no in-place branch switching. Promoted from a free-form string in the
    /// e2e-smoketest spec.
    /// </summary>
    public Guid BranchId { get; set; }

    /// <summary>Navigation to the pinned branch.</summary>
    public ProjectBranch Branch { get; set; } = null!;

    /// <summary>
    /// Tenant scope for future per-tenant policies (idle thresholds, region
    /// pinning, quota). Nullable for now — single-tenant deployments leave
    /// this empty and we attach a tenant later without a backfill.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Current lifecycle state. Persisted as a string so adding new states
    /// later doesn't break existing rows. Indexed because background workers
    /// constantly query "all runtimes in state X".
    /// </summary>
    public RuntimeState State { get; set; } = RuntimeState.Pending;

    /// <summary>UTC timestamp of the last <see cref="State"/> change. Defaults to creation time.</summary>
    public DateTime StateChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Box id once provisioning has forked one from the template. Indexed so
    /// the reconciler can resolve "which runtime owns this box?" in O(1).
    /// Unlike the Fly-era machine id, the box IS the persistence — its disk
    /// survives stop/resume — so this id is stable for the runtime's whole
    /// life (only ResetFromScratch and size-change reprovision replace it).
    /// </summary>
    public string? BoxId { get; set; }

    /// <summary>
    /// Id of the golden template box this runtime was forked from (see the
    /// RuntimeTemplates feature). Audit-only — respawns resume the existing
    /// box rather than re-forking, so this never drives a boot after the
    /// first provision.
    /// </summary>
    public string? TemplateBoxId { get; set; }

    /// <summary>Box region the VM lives in (EU: e.g. <c>"de"</c>, <c>"fi"</c>, <c>"fr"</c>). Informational — forks inherit the template's region.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Requested disk size in gigabytes, snapshotted from
    /// <c>Project.RuntimeVolumeSizeGb</c>. Informational on Box — every box
    /// ships &gt;50 GB of disk regardless — but retained so the requested spec
    /// stays auditable and pre-Box rows keep their history.
    /// </summary>
    public int VolumeSizeGb { get; set; } = 5;

    /// <summary>
    /// Legacy CPU-class label snapshotted from <c>Project.RuntimeCpuKind</c>.
    /// Box has no CPU-class notion (its tiers are picked from
    /// <see cref="Cpus"/> + <see cref="MemoryMb"/> via <c>BoxTypeMapper</c>);
    /// retained for audit of the requested spec only.
    /// </summary>
    public string CpuKind { get; set; } = Project.DefaultRuntimeCpuKind;

    /// <summary>
    /// Requested vCPU count, snapshotted from <c>Project.RuntimeCpus</c>.
    /// Together with <see cref="MemoryMb"/> this picks the Box size tier
    /// (small 2/4, default 4/8, large 8/16) via <c>BoxTypeMapper</c> — the
    /// mapping rounds up so a runtime never lands on a smaller box than asked.
    /// </summary>
    public int Cpus { get; set; } = Project.DefaultRuntimeCpus;

    /// <summary>Requested RAM in MiB, snapshotted from <c>Project.RuntimeMemoryMb</c>. See <see cref="Cpus"/>.</summary>
    public int MemoryMb { get; set; } = Project.DefaultRuntimeMemoryMb;

    /// <summary>
    /// Per-runtime override of the global idle-suspend threshold, in minutes.
    /// <c>null</c> means "use the tenant or global default".
    /// </summary>
    public int? IdleThresholdMinutes { get; set; }

    /// <summary>
    /// How many times the supervisor has respawned this runtime after a
    /// <see cref="RuntimeState.Crashed"/>. Resets on a successful boot.
    /// </summary>
    public int RespawnRetries { get; set; }

    /// <summary>
    /// UTC timestamp of the last heartbeat the daemon sent. Drives idle
    /// detection; <c>null</c> until the runtime first boots successfully.
    /// </summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// The agent session the daemon reports it is actively executing right now,
    /// straight from <c>HeartbeatPayload.activeSessionId</c>. <c>null</c> when the
    /// daemon is idle (no run in flight). Overwritten on every heartbeat — this is
    /// the daemon's authoritative "the run I am driving is X" signal, and the
    /// <c>ReconcileStaleSessionsJob</c> uses it to reap sessions stuck Running /
    /// Canceling on a provably-alive runtime that is no longer executing them
    /// (e.g. the terminal completion event was lost when the cursor subprocess
    /// died mid-stream). Deliberately a plain nullable Guid — not an FK — so a
    /// reported-but-since-deleted session id never trips a cascade or constraint.
    /// </summary>
    public Guid? ActiveSessionId { get; set; }

    // -------- Observability snapshots (super-admin polish) --------
    //
    // The five columns below are "latest snapshot" buckets — overwritten on
    // every heartbeat by the daemon's sysstats / disk / supervisord pollers.
    // We intentionally do NOT log every sample to RuntimeEvent (too noisy);
    // discrete transitions (e.g. DiskPressureCritical) still emit events, but
    // the current values live here so the drawer's Sysstats panel can read a
    // single row on cold load. All nullable — pre-observability runtimes (or
    // brand-new rows that haven't heartbeated yet) leave them blank.

    /// <summary>
    /// Most recently reported bytes used on the runtime's <c>/data</c> volume
    /// (the daemon's <c>DiskMonitor</c> latest sample). Null until the first
    /// heartbeat carrying a disk snapshot lands.
    /// </summary>
    public long? LastDiskUsedBytes { get; set; }

    /// <summary>
    /// Most recently reported total bytes on the runtime's <c>/data</c> volume.
    /// Null until the first heartbeat with a disk snapshot lands. Paired with
    /// <see cref="LastDiskUsedBytes"/> — readers display
    /// <c>used / total</c> together or skip both when either is null.
    /// </summary>
    public long? LastDiskTotalBytes { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent disk sample. Lets the drawer caption
    /// the Sysstats panel with freshness ("Disk sampled 8s ago"); the daemon's
    /// /proc reads are cheap so this should be at most a heartbeat-interval old.
    /// </summary>
    public DateTime? LastDiskSampledAt { get; set; }

    /// <summary>
    /// Most recent per-process sysstats snapshot pushed by the daemon's
    /// heartbeat — JSON shape <c>{ sampledAt, processes: [ { name, pid,
    /// rssBytes, cpuPercent } ], networkRxBytes, networkTxBytes,
    /// rxBytesPerSec, txBytesPerSec }</c> (see spec section B6 + B14).
    /// Overwritten every heartbeat — no history. Null until the first
    /// heartbeat with sysstats lands. Stored as <c>jsonb</c>.
    /// </summary>
    public string? LastSysstatsSnapshot { get; set; }

    /// <summary>
    /// Most recent supervisord live-state snapshot pushed by the daemon —
    /// JSON shape <c>{ sampledAt, processes: [ { name, state, pid, uptimeMs,
    /// exitStatus, spawnErr, lastStopAt } ] }</c> (see spec section B1).
    /// Persisted (not just live-pushed) so the drawer cold-load can render
    /// the Services tab without waiting for the next supervisord tick.
    /// Overwritten on every tick. Null until the first poll lands. Stored
    /// as <c>jsonb</c>.
    /// </summary>
    public string? LastSupervisordSnapshot { get; set; }

    // NOTE: The mutable runtime spec (Spec + SpecVersion) was moved to
    // Project per the `project-level-runtime-spec` spec. One project, one
    // spec, all branches inherit. See Project.Spec / Project.SpecVersion.

    /// <summary>
    /// Health of this runtime's <i>spec application</i>, decoupled from
    /// <see cref="State"/>. A runtime can be <see cref="RuntimeState.Online"/>
    /// (alive) while its spec only partially applied — that runtime is
    /// <see cref="RuntimeSpecHealth.Degraded"/>. Reported best-effort by the
    /// daemon via <c>RuntimeHub.ReportSpecHealth</c> after boot, and flipped
    /// back to <see cref="RuntimeSpecHealth.Healthy"/> by the self-healing
    /// repair loop. Defaults to <see cref="RuntimeSpecHealth.Unknown"/> until a
    /// report lands. Boot-issue <i>details</i> are NOT stored here — they live in
    /// <c>RuntimeEvents</c> / <c>RuntimeErrorReports</c>. Persisted as
    /// <c>varchar(16)</c> (string conversion) with a DB default of
    /// <c>'Unknown'</c>; see <see cref="RuntimeSpecHealth"/>.
    /// </summary>
    public RuntimeSpecHealth SpecHealth { get; set; } = RuntimeSpecHealth.Unknown;

    /// <summary>
    /// UTC timestamp of the last bootstrap-progress signal observed for this
    /// runtime — bumped by <c>RecordRuntimeEventCommandHandler</c> on every
    /// <c>RuntimeEvent</c> insert while the runtime is mid-boot
    /// (<see cref="RuntimeState.Booting"/> / <see cref="RuntimeState.Bootstrapping"/> /
    /// <see cref="RuntimeState.Waking"/>).
    ///
    /// <para><b>Why this exists — silence-based liveness.</b> Bootstrap progress
    /// (clone, <c>dotnet restore</c>, build, install, service start) is streamed
    /// to <c>RuntimeEvents</c> only; none of it touches the
    /// <see cref="ProjectRuntime"/> row, so <see cref="UpdatedAt"/> stays frozen
    /// at the time of the last state transition. <see cref="HeartbeatWatcherJob"/>'s
    /// bootstrap-timeout branch used that frozen <see cref="UpdatedAt"/> as a
    /// "time in state" proxy and Crashed runtimes that were busy-but-quiet on the
    /// row — a real .NET first boot (<c>dotnet restore</c> ~5 min + build) tripped
    /// the timeout while it was actively streaming build output, respawned, and
    /// looped forever without ever reaching Online-degraded. This column lets the
    /// watchdog measure <i>silence</i> (no bootstrap activity) instead of total
    /// time-in-state: as long as events keep flowing the runtime is provably
    /// alive. Cleared when a fresh boot cycle starts (<see cref="RuntimeState.Pending"/>,
    /// <see cref="RuntimeState.Booting"/>, <see cref="RuntimeState.Waking"/>) so a
    /// stale timestamp from a prior successful boot cannot make
    /// <see cref="HeartbeatWatcherJob"/> instant-timeout a respawn. Until the first
    /// mid-boot event lands, the watchdog coalesce
    /// <c>(LastBootstrapActivityAt ?? UpdatedAt)</c> falls back to the state-change
    /// time. Deliberately NOT bumped via the audit pipeline (no
    /// <see cref="UpdatedAt"/> change) so the activity signal stays orthogonal to
    /// the row's audit timestamp.
    /// </summary>
    public DateTime? LastBootstrapActivityAt { get; set; }

    // -------- Self-healing repair consent (self-healing-runtime-specs, B2/B3) --------
    //
    // The five columns below drive the "let agent fix it" repair loop and its
    // BUDGETED auto-apply consent. All already exist on the platform DB
    // (43594/app) from a prior, now-lost migration; the recovery migration's
    // idempotent ADD COLUMN IF NOT EXISTS keeps fresh runtime DBs in sync.

    /// <summary>
    /// Repair consent armed: the next agent-authored <c>RuntimeProposal</c> for
    /// this runtime auto-applies (no second click) via the existing approve+apply
    /// path. Set <c>true</c> by <c>RepairRuntimeCommand</c> when the operator
    /// clicks "Let agent fix it"; gated additionally on
    /// <see cref="AutoApplyExpiresAt"/> (window) and
    /// <see cref="AutoApplyAttemptsRemaining"/> (budget). Cleared when the budget
    /// hits 0, when a repair apply succeeds, or if the dispatch itself failed.
    /// Defaults to <c>false</c> — auto-apply is opt-in per repair.
    /// </summary>
    public bool AutoApplyNextProposal { get; set; }

    /// <summary>
    /// UTC expiry of the repair consent window (~30 min from arming). Even with
    /// budget remaining, a proposal that arrives after this instant does NOT
    /// auto-apply — a stale consent must never silently apply an agent's spec
    /// hours later. <c>null</c> when consent is not armed / has been cleared.
    /// </summary>
    public DateTime? AutoApplyExpiresAt { get; set; }

    /// <summary>
    /// Budgeted consent counter. A single repair turn can produce multiple
    /// propose→apply→fail→correct cycles; if the FIRST apply fails (e.g. a
    /// supervisord <c>%Y</c>), the agent's corrected retry must STILL auto-apply.
    /// So consent survives a failed apply, bounded by this counter (and the
    /// <see cref="AutoApplyExpiresAt"/> window). Each auto-applied proposal
    /// decrements it; <see cref="AutoApplyNextProposal"/> clears only when this
    /// hits 0. Armed to <c>MaxAutoApplyAttempts</c> (3); default <c>0</c> = not armed.
    /// </summary>
    public int AutoApplyAttemptsRemaining { get; set; }

    /// <summary>
    /// Loop-guard counter: how many times <c>RepairRuntimeCommand</c> has
    /// dispatched a repair turn for this runtime. Compared against a sane cap so a
    /// runtime that keeps degrading can't get an unbounded fan of repair turns.
    /// Defaults to <c>0</c>.
    /// </summary>
    public int RepairAttempts { get; set; }

    /// <summary>
    /// UTC timestamp of the last repair dispatch — paired with
    /// <see cref="RepairAttempts"/> for the windowed loop guard. <c>null</c>
    /// until the first repair is requested.
    /// </summary>
    public DateTime? LastRepairAttemptAt { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>
    /// Move the runtime from its current <see cref="State"/> to <paramref name="newState"/>.
    /// The transition is validated against <see cref="RuntimeStateMachine"/>; illegal moves
    /// return <see cref="Result.Failure(string)"/> without mutating any state.
    ///
    /// <para>On success this:</para>
    /// <list type="bullet">
    ///   <item>updates <see cref="State"/> and <see cref="StateChangedAt"/>;</item>
    ///   <item>resets <see cref="RespawnRetries"/> to 0 when the new state is
    ///         <see cref="RuntimeState.Online"/> — a healthy run wipes the slate;</item>
    ///   <item>clears <see cref="LastHeartbeatAt"/> on entry to any non-<see cref="RuntimeState.Online"/>
    ///         state — the daemon-connection invariant is broken (boot, suspend, wake, crash, etc.),
    ///         so a stale heartbeat from a previous daemon process must not bleed across the
    ///         disconnect window. Without this, e.g. <see cref="HeartbeatWatcherJob"/> instantly
    ///         flags a Suspended → Waking runtime as Crashed because <c>LastHeartbeatAt</c> still
    ///         holds the value from its prior Online session;</item>
    ///   <item>clears <see cref="LastBootstrapActivityAt"/> on entry to
    ///         <see cref="RuntimeState.Pending"/>, <see cref="RuntimeState.Booting"/>, or
    ///         <see cref="RuntimeState.Waking"/> — a fresh boot cycle must not inherit the
    ///         activity timestamp from a prior Online run, or the bootstrap-silence watchdog
    ///         can Crashed a respawn seconds after machine creation;</item>
    ///   <item>raises a <see cref="RuntimeStateChanged"/> domain event so the
    ///         <c>PersistRuntimeStateEventHandler</c> writes an audit row.</item>
    /// </list>
    /// </summary>
    public Result TransitionTo(RuntimeState newState, string reason, string triggeredBy, string? metadata = null)
    {
        if (!RuntimeStateMachine.CanTransition(State, newState))
        {
            return Result.Failure($"Illegal transition: {State} -> {newState}");
        }

        var fromState = State;
        State = newState;
        StateChangedAt = DateTime.UtcNow;

        // Reset retry counter when we successfully reach Online — a healthy run wipes the slate
        if (newState == RuntimeState.Online)
        {
            RespawnRetries = 0;
        }

        // Clear LastHeartbeatAt on every transition that breaks the daemon-connection
        // invariant. The only state where the daemon is guaranteed to be continuously
        // heartbeating is Online; any move out of (or never into) Online means the
        // daemon process is starting, stopping, restarting, gone, or being torn down.
        // The next heartbeat the daemon submits after the new connection refills this.
        // The HeartbeatWatcherJob's `LastHeartbeatAt != null` filter then correctly
        // excludes the runtime during the cold-start / reconnect window.
        if (newState != RuntimeState.Online)
        {
            LastHeartbeatAt = null;
        }

        if (newState is RuntimeState.Pending or RuntimeState.Booting or RuntimeState.Waking)
        {
            LastBootstrapActivityAt = null;
        }

        RaiseDomainEvent(new RuntimeStateChanged(
            Id, ProjectId, BranchId, fromState, newState, reason, triggeredBy, metadata, StateChangedAt));

        return Result.Success();
    }

    /// <summary>
    /// User-initiated restart for a runtime stuck in <see cref="RuntimeState.Failed"/>
    /// or <see cref="RuntimeState.Crashed"/>. Walks the runtime to <c>Pending</c> so
    /// the <c>RuntimeProvisionerJob</c> picks the row up on its next tick and
    /// reboots the existing box (stop + resume) — the box's disk IS the user's
    /// working data, so it survives the restart by construction.
    ///
    /// <list type="bullet">
    ///   <item>Legal from any live runtime the user might want to hard-reboot:
    ///         <see cref="RuntimeState.Online"/>, mid-boot states
    ///         (<see cref="RuntimeState.Booting"/> /
    ///         <see cref="RuntimeState.Bootstrapping"/> /
    ///         <see cref="RuntimeState.Waking"/>), or failure states
    ///         (<see cref="RuntimeState.Failed"/> /
    ///         <see cref="RuntimeState.Crashed"/>). Any other source state
    ///         returns a <see cref="Result.Failure(string)"/> without mutating
    ///         anything.</item>
    ///   <item>Resets <see cref="RespawnRetries"/> to 0 — the operator is explicitly
    ///         re-arming the runtime's failure budget for the new boot attempt.</item>
    ///   <item><b>Deliberately keeps <see cref="BoxId"/>.</b> The provisioner's
    ///         existing-box path stops and resumes that box; only
    ///         <see cref="ResetFromScratch"/> abandons it.</item>
    ///   <item>Raises <see cref="RuntimeStateChanged"/> with <c>reason="user_restart"</c>
    ///         and <c>triggeredBy="user:{userId}"</c> so the audit trail in
    ///         <c>RuntimeStateEvents</c> attributes the action to the right user.</item>
    /// </list>
    /// </summary>
    public bool HardwareSpecMatches(string cpuKind, int cpus, int memoryMb, int volumeSizeGb) =>
        string.Equals(CpuKind, cpuKind, StringComparison.Ordinal)
        && Cpus == cpus
        && MemoryMb == memoryMb
        && VolumeSizeGb == volumeSizeGb;

    /// <summary>
    /// Snapshots new hardware sizing onto this row and walks to
    /// <see cref="RuntimeState.Pending"/> so <see cref="Jobs.RuntimeProvisionerJob"/>
    /// re-sizes the runtime. On Box a size change is a disk-preserving fork:
    /// stop the current box (fresh snapshot), fork it at the new size tier,
    /// delete the old box. <see cref="BoxId"/> is therefore kept here — the
    /// provisioner needs it as the fork source.
    /// </summary>
    public Result ReprovisionAfterSpecChange(
        string cpuKind,
        int cpus,
        int memoryMb,
        int volumeSizeGb,
        Guid userId)
    {
        if (State is RuntimeState.Deleting or RuntimeState.Deleted or RuntimeState.Suspending)
        {
            return Result.Failure(
                $"Cannot apply spec to runtime in state {State}; wait for the current operation to finish.");
        }

        CpuKind = cpuKind;
        Cpus = cpus;
        MemoryMb = memoryMb;
        VolumeSizeGb = volumeSizeGb;

        var fromState = State;
        State = RuntimeState.Pending;
        RespawnRetries = 0;
        StateChangedAt = DateTime.UtcNow;
        LastHeartbeatAt = null;
        LastBootstrapActivityAt = null;

        RaiseDomainEvent(new RuntimeStateChanged(
            RuntimeId: Id,
            ProjectId: ProjectId,
            BranchId: BranchId,
            FromState: fromState,
            ToState: State,
            Reason: "spec:apply_to_existing",
            TriggeredBy: $"user:{userId}",
            Metadata: null,
            OccurredAt: StateChangedAt));

        return Result.Success();
    }

    public Result Restart(Guid userId)
    {
        if (State is not (
            RuntimeState.Online
            or RuntimeState.Booting
            or RuntimeState.Bootstrapping
            or RuntimeState.Waking
            or RuntimeState.Failed
            or RuntimeState.Crashed))
        {
            return Result.Failure(
                $"Cannot restart runtime in state {State}; must be Online, mid-boot, Failed, or Crashed.");
        }

        var fromState = State;
        State = RuntimeState.Pending;
        RespawnRetries = 0;
        StateChangedAt = DateTime.UtcNow;
        // NOTE: BoxId is intentionally NOT cleared — the provisioner's
        // existing-box path stops + resumes that box, and its disk carries the
        // user's working data. That's the whole point of restart vs a full
        // reset-from-scratch. See RuntimeProvisionerJob.ProvisionAsync.

        // Same daemon-connection-invariant reasoning as TransitionTo: a runtime
        // leaving Failed has no live daemon, so stale heartbeats must not leak
        // across into the next boot's heartbeat watcher window.
        LastHeartbeatAt = null;
        LastBootstrapActivityAt = null;

        RaiseDomainEvent(new RuntimeStateChanged(
            RuntimeId: Id,
            ProjectId: ProjectId,
            BranchId: BranchId,
            FromState: fromState,
            ToState: State,
            Reason: "user_restart",
            TriggeredBy: $"user:{userId}",
            Metadata: null,
            OccurredAt: StateChangedAt));

        return Result.Success();
    }

    /// <summary>
    /// User-initiated full reprovision: drop the box reference and walk to
    /// <see cref="RuntimeState.Pending"/> so <see cref="Jobs.RuntimeProvisionerJob"/>
    /// forks a fresh box from the active template. Unlike <see cref="Restart"/>,
    /// this abandons the box AND its disk — the escape hatch when the box is
    /// gone, wedged, or its filesystem is beyond repair. The caller deletes the
    /// old box best-effort before invoking this.
    /// </summary>
    public Result ResetFromScratch(Guid userId)
    {
        if (State is RuntimeState.Deleting or RuntimeState.Deleted or RuntimeState.Suspending)
        {
            return Result.Failure(
                $"Cannot reset runtime in state {State}; wait for the current operation to finish.");
        }

        var fromState = State;
        State = RuntimeState.Pending;
        BoxId = null;
        TemplateBoxId = null;
        RespawnRetries = 0;
        StateChangedAt = DateTime.UtcNow;
        LastHeartbeatAt = null;
        LastBootstrapActivityAt = null;

        RaiseDomainEvent(new RuntimeStateChanged(
            RuntimeId: Id,
            ProjectId: ProjectId,
            BranchId: BranchId,
            FromState: fromState,
            ToState: State,
            Reason: "user_reset_from_scratch",
            TriggeredBy: $"user:{userId}",
            Metadata: null,
            OccurredAt: StateChangedAt));

        return Result.Success();
    }
}
