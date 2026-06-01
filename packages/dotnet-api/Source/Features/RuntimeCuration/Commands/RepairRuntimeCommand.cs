using System.Text;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.Conversations.Services;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// The "Let agent fix it" self-heal trigger (self-healing-runtime-specs, cards
/// B2 + B3). The operator clicks the amber degraded-banner's repair button →
/// <c>POST /api/runtimes/{runtimeId}/repair</c> → this command. We:
///
/// <list type="number">
///   <item>Loop-guard: bump <see cref="ProjectRuntime.RepairAttempts"/> +
///         <see cref="ProjectRuntime.LastRepairAttemptAt"/>; refuse with a typed
///         <c>repair_attempts_exhausted</c> result (no dispatch) once the
///         windowed cap (<see cref="MaxRepairAttempts"/>) is hit.</item>
///   <item>Compose a rich plain-text diagnostic prompt — which stage/service
///         failed, the failing reasons/logs (from boot-issue
///         <see cref="RuntimeEvent"/>s), the current project spec JSON, and an
///         instruction to use <c>get_boot_issues</c> + <c>get_runtime_spec</c>,
///         validate with <c>dry_run_install</c>, then <c>propose_runtime_spec</c>
///         — telling the agent its proposal will auto-apply (no second click).</item>
///   <item>Dispatch a SYSTEM turn into the runtime's conversation via
///         <see cref="ITurnDispatcher.DispatchTurnAsync"/>.</item>
///   <item>Arm BUDGETED consent:
///         <see cref="ProjectRuntime.AutoApplyNextProposal"/> = true,
///         <see cref="ProjectRuntime.AutoApplyExpiresAt"/> = now + 30 min,
///         <see cref="ProjectRuntime.AutoApplyAttemptsRemaining"/> =
///         <see cref="MaxAutoApplyAttempts"/>. The consent is consumed in
///         <see cref="CreateRuntimeProposalCommandHandler"/> and cleared on first
///         successful apply by <see cref="RecordApplyResultCommandHandler"/>.</item>
/// </list>
///
/// <para>If the dispatch itself throws (no runtime for branch, missing
/// conversation, hub blow-up) we DISARM the consent in the catch — an armed
/// runtime with no in-flight repair turn would auto-apply the next unrelated
/// proposal, which is exactly the surprise budgeted consent exists to avoid.</para>
/// </summary>
public record RepairRuntimeCommand(
    Guid RuntimeId,
    string ActorUserId) : ICommand<Result<RepairRuntimeResponse>>;

public record RepairRuntimeResponse(
    Guid RuntimeId,
    Guid SessionId,
    Guid ConversationId,
    bool Queued,
    int RepairAttempt,
    DateTime AutoApplyExpiresAt,
    int AutoApplyAttemptsRemaining);

public class RepairRuntimeCommandHandler
    : ICommandHandler<RepairRuntimeCommand, Result<RepairRuntimeResponse>>
{
    /// <summary>
    /// Budget for repair-driven auto-applies armed per repair click. A single
    /// repair turn may produce propose→apply→fail→correct cycles; this many
    /// auto-applies are permitted within the consent window before the operator
    /// must click again. Mirrors the daemon-side self-heal continuation budget (3).
    /// </summary>
    public const int MaxAutoApplyAttempts = 3;

    /// <summary>
    /// Consent window — even with budget remaining, a proposal arriving after
    /// this many minutes does not auto-apply. 30 min comfortably covers a slow
    /// agent diagnose+dry-run+propose loop without leaving a stale armed runtime.
    /// </summary>
    public const int ConsentWindowMinutes = 30;

    /// <summary>
    /// Loop guard: max repair dispatches counted within
    /// <see cref="RepairWindowMinutes"/>. Past this we refuse rather than fan out
    /// yet another repair turn for a runtime that keeps degrading.
    /// </summary>
    public const int MaxRepairAttempts = 5;

    /// <summary>
    /// Sliding window for the loop guard. A repair burst older than this no
    /// longer counts toward <see cref="MaxRepairAttempts"/> — measured off
    /// <see cref="ProjectRuntime.LastRepairAttemptAt"/>. Keeps a runtime that
    /// degrades again hours later eligible for a fresh repair budget.
    /// </summary>
    public const int RepairWindowMinutes = 60;

    /// <summary>
    /// Boot-issue event types the diagnostic prompt summarises. Mirrors the
    /// read surface B1 added to <c>RuntimeStatusController</c>.
    /// </summary>
    private static readonly string[] BootIssueTypes =
    {
        RuntimeEventTypes.InstallFailed,
        RuntimeEventTypes.ServiceCrashed,
        RuntimeEventTypes.ServiceFailedToStart,
        RuntimeEventTypes.SpecDeltaFailed,
        RuntimeEventTypes.ServiceEnvMissing,
        RuntimeEventTypes.SpecDegraded,
    };

    private readonly ApplicationDbContext _db;
    private readonly ITurnDispatcher _turnDispatcher;
    private readonly ILogger<RepairRuntimeCommandHandler> _logger;

    public RepairRuntimeCommandHandler(
        ApplicationDbContext db,
        ITurnDispatcher turnDispatcher,
        ILogger<RepairRuntimeCommandHandler> logger)
    {
        _db = db;
        _turnDispatcher = turnDispatcher;
        _logger = logger;
    }

    public async Task<Result<RepairRuntimeResponse>> Handle(
        RepairRuntimeCommand request,
        CancellationToken cancellationToken)
    {
        // Tracked load — we mutate the loop-guard + consent columns. The global
        // soft-delete filter hides torn-down runtimes (can't repair a dead one).
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == request.RuntimeId, cancellationToken);
        if (runtime is null)
        {
            return Result.Failure<RepairRuntimeResponse>("not_found");
        }

        var nowUtc = DateTime.UtcNow;

        // Windowed loop guard. If the last repair was inside the window, the
        // attempts accumulate; if it's older (or never), the counter resets so a
        // runtime that degrades again much later gets a fresh budget. Refuse
        // BEFORE any dispatch or consent arming.
        var withinWindow = runtime.LastRepairAttemptAt is { } last
            && (nowUtc - last) < TimeSpan.FromMinutes(RepairWindowMinutes);
        var priorAttempts = withinWindow ? runtime.RepairAttempts : 0;
        if (priorAttempts >= MaxRepairAttempts)
        {
            _logger.LogWarning(
                "RepairRuntime: runtime {RuntimeId} exhausted repair budget ({Attempts}/{Cap} within {Window}m); refusing dispatch.",
                runtime.Id, priorAttempts, MaxRepairAttempts, RepairWindowMinutes);
            return Result.Failure<RepairRuntimeResponse>("repair_attempts_exhausted");
        }

        // Gather diagnostics for the prompt — recent boot-issue events + current
        // project spec. Read-only; failures here shouldn't sink the repair, so we
        // tolerate empty lists and a null spec (the agent's own get_boot_issues /
        // get_runtime_spec tools are the authoritative source anyway).
        var bootIssues = await _db.RuntimeEvents
            .AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id && BootIssueTypes.Contains(e.Type))
            .OrderByDescending(e => e.Timestamp)
            .Take(20)
            .Select(e => new BootIssueRow(e.Type, e.Severity.ToString(), e.Timestamp, e.Payload))
            .ToListAsync(cancellationToken);

        var currentSpecJson = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => p.Spec)
            .FirstOrDefaultAsync(cancellationToken);

        var prompt = BuildDiagnosticPrompt(runtime, bootIssues, currentSpecJson);

        // Resolve (or create) the conversation the repair turn lands in. The
        // repair endpoint is keyed by runtime, not conversation, so reuse the
        // most-recent non-archived conversation for this project+branch (the
        // user's active thread) or open a dedicated repair conversation.
        var conversation = await ResolveRepairConversationAsync(runtime, nowUtc, cancellationToken);

        // ARM consent + bump the loop guard FIRST, in the same change-tracker as
        // the runtime load, then save — so the consent is visible to the proposal
        // handler the moment the agent's propose_runtime_spec lands. The dispatch
        // happens AFTER this save (same ordering the AgentHub/RuntimeHub paths
        // use). If dispatch throws, the catch disarms.
        runtime.RepairAttempts = priorAttempts + 1;
        runtime.LastRepairAttemptAt = nowUtc;
        runtime.AutoApplyNextProposal = true;
        runtime.AutoApplyExpiresAt = nowUtc.AddMinutes(ConsentWindowMinutes);
        runtime.AutoApplyAttemptsRemaining = MaxAutoApplyAttempts;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var dispatch = await _turnDispatcher.DispatchTurnAsync(
                new DispatchTurnArgs(
                    ConversationId: conversation.Id,
                    ProjectId: runtime.ProjectId,
                    BranchId: runtime.BranchId,
                    Prompt: prompt,
                    AgentId: null,
                    // System-initiated, but attributed to the operator who clicked
                    // repair so the audit trail names a human.
                    EventOriginUserId: request.ActorUserId),
                cancellationToken);

            _logger.LogInformation(
                "RepairRuntime: dispatched repair turn (session {SessionId}, queued={Queued}) for runtime {RuntimeId}; consent armed (budget={Budget}, expires={Expires:o}), attempt {Attempt}/{Cap}.",
                dispatch.SessionId, dispatch.Queued, runtime.Id,
                runtime.AutoApplyAttemptsRemaining, runtime.AutoApplyExpiresAt, runtime.RepairAttempts, MaxRepairAttempts);

            return Result.Success(new RepairRuntimeResponse(
                RuntimeId: runtime.Id,
                SessionId: dispatch.SessionId,
                ConversationId: conversation.Id,
                Queued: dispatch.Queued,
                RepairAttempt: runtime.RepairAttempts,
                AutoApplyExpiresAt: runtime.AutoApplyExpiresAt!.Value,
                AutoApplyAttemptsRemaining: runtime.AutoApplyAttemptsRemaining));
        }
        catch (Exception ex)
        {
            // Dispatch failed — there is no repair turn in flight, so an armed
            // runtime would auto-apply the next unrelated proposal. Disarm the
            // consent (leave the loop-guard bump — the attempt did happen) so we
            // fail closed. The loop-guard counter staying bumped is intentional:
            // a flapping dispatch shouldn't be retryable an unbounded number of
            // times either.
            runtime.AutoApplyNextProposal = false;
            runtime.AutoApplyExpiresAt = null;
            runtime.AutoApplyAttemptsRemaining = 0;
            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogError(ex,
                "RepairRuntime: dispatch failed for runtime {RuntimeId}; consent disarmed.",
                runtime.Id);

            return Result.Failure<RepairRuntimeResponse>("dispatch_failed");
        }
    }

    /// <summary>
    /// Most-recent non-archived conversation for the runtime's project+branch,
    /// or a freshly-created dedicated repair conversation when none exists. The
    /// new row is flushed before the dispatcher runs (the dispatcher does its own
    /// SaveChanges for the session + event) — same ordering as
    /// <c>AgentHub.SubmitPrompt</c>.
    /// </summary>
    private async Task<Conversation> ResolveRepairConversationAsync(
        ProjectRuntime runtime,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var existing = await _db.Conversations
            .Where(c => c.ProjectId == runtime.ProjectId && c.BranchId == runtime.BranchId)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var conversation = new Conversation
        {
            ProjectId = runtime.ProjectId,
            BranchId = runtime.BranchId,
            Title = "Runtime repair",
            Status = ConversationStatus.Active,
            LastActivityAt = nowUtc,
            EventCount = 0,
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);
        return conversation;
    }

    /// <summary>
    /// Compose the plain-text diagnostic prompt. <see cref="StartTurnPayload"/>
    /// carries only a string, so everything the agent needs to start reasoning
    /// goes inline — but we deliberately steer it to the live MCP tools
    /// (<c>get_boot_issues</c>, <c>get_runtime_spec</c>, <c>dry_run_install</c>)
    /// for the authoritative, current view rather than trusting this snapshot.
    /// </summary>
    private static string BuildDiagnosticPrompt(
        ProjectRuntime runtime,
        IReadOnlyList<BootIssueRow> bootIssues,
        string? currentSpecJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[SYSTEM: RUNTIME SELF-HEAL REQUEST]");
        sb.AppendLine();
        sb.AppendLine(
            "This runtime reached Online but its runtime spec did NOT fully apply — it is in a DEGRADED spec-health state. " +
            "One or more non-critical bootstrap stages (Install / RunningSetup / StartingServices) failed deterministically during boot. " +
            "Your job: diagnose why, author a corrected runtime spec, and propose it. Your proposal will AUTO-APPLY — there is no second confirmation click — so be precise.");
        sb.AppendLine();

        sb.AppendLine("## What failed (recent boot-issue events)");
        if (bootIssues.Count == 0)
        {
            sb.AppendLine(
                "No boot-issue events were found in the recent event window. Use the get_boot_issues tool to read the live in-memory boot issues directly.");
        }
        else
        {
            foreach (var issue in bootIssues)
            {
                sb.Append("- ")
                  .Append(issue.Type)
                  .Append(" (")
                  .Append(issue.Severity)
                  .Append(", ")
                  .Append(issue.Timestamp.ToString("o"))
                  .Append("): ")
                  .AppendLine(Truncate(issue.Payload, 1500));
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Current applied runtime spec (project-level source of truth)");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(currentSpecJson)
                ? "(no spec recorded on the project — confirm with the get_runtime_spec tool)"
                : Truncate(currentSpecJson, 6000));
        sb.AppendLine();

        sb.AppendLine("## How to fix it");
        sb.AppendLine("1. Call get_boot_issues to read the authoritative in-memory boot issues for this runtime (stage, service, reason, detail).");
        sb.AppendLine("2. Call get_runtime_spec to read the exact current applied spec + version.");
        sb.AppendLine("3. Work out the deterministic fix (bad install snippet, a service that never binds, a missing required env var, a supervisord '%' crash, etc.).");
        sb.AppendLine("4. Validate the fix with dry_run_install BEFORE proposing — do not propose a spec you have not dry-run validated.");
        sb.AppendLine("5. Call propose_runtime_spec with the corrected spec. It will auto-apply live (no reboot, no second click) via the existing delta-apply path, flipping SpecHealth Degraded -> Healthy on success.");
        sb.AppendLine();
        sb.AppendLine(
            "If your first corrected spec still fails to apply, the consent budget lets your NEXT corrected proposal auto-apply too — iterate until the spec applies cleanly. " +
            "If the failure is not something a spec change can fix (e.g. an external service is down), say so explicitly instead of proposing.");

        return sb.ToString();
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length <= max ? value : value.Substring(0, max) + "…(truncated)";
    }

    private readonly record struct BootIssueRow(
        string Type,
        string Severity,
        DateTime Timestamp,
        string Payload);
}
