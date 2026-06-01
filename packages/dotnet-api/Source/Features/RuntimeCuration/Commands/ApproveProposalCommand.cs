using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Services;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// User-initiated decision on a pending <see cref="RuntimeProposal"/>: accept
/// the daemon's proposed V3 spec verbatim, write it through to the
/// <b>project</b>'s persisted spec, and push the daemon-bound V2 delta
/// (computed against the project's prior expanded spec) to the proposal's
/// originating runtime for install via the supervisord renderer.
///
/// <list type="number">
///   <item>Load the proposal scoped to <see cref="ProjectId"/>; not found / cross-project → <c>not_found</c>.</item>
///   <item>Status guard: must be <see cref="RuntimeProposalStatus.Pending"/> → otherwise <c>already_decided</c>.</item>
///   <item>Stamp <c>Status=Approved</c>, <c>AppliedSpec=ProposedSpec</c> (V3), <c>DecidedBy/At</c>.</item>
///   <item>Compute the V2 delta of the proposal's persisted <see cref="RuntimeProposal.ExpandedSpec"/>
///         (the V2 produced by the expander at propose-time) against the project's prior V2 expansion.</item>
///   <item>Replace <c>Project.Spec</c> with the V3 proposed spec (source of truth); bump <c>Project.SpecVersion</c>.</item>
///   <item>Push <see cref="IRuntimeClient.ApplyRuntimeSpecDelta"/> with the V2 delta to <c>runtime-{RuntimeId}</c>
///         (the proposal's originating runtime ONLY — lazy propagation contract, other runtimes converge on next cold boot via GetBootstrap).</item>
///   <item>Broadcast <see cref="IAgentClient.RuntimeProposalUpdated"/> to <c>project-{ProjectId}</c>.</item>
/// </list>
///
/// <para><b>V3 → V2 split.</b> The project stores the V3 source of truth so
/// the next propose cycle has the high-fidelity input shape to diff against.
/// The daemon receives V2 because that's the daemon's wire format — we
/// expand server-side and persist the V2 on the proposal row at
/// propose-time, then read it back here to avoid re-expanding (which would
/// also avoid drift if a preset was edited between propose + approve).</para>
///
/// <para>The daemon's eventual ack lands on
/// <see cref="SignalR.Hubs.RuntimeHub.RuntimeSpecDeltaApplied"/>, which routes
/// through <see cref="RecordApplyResultCommand"/> to flip the row to
/// <see cref="RuntimeProposalStatus.Applied"/> or
/// <see cref="RuntimeProposalStatus.Failed"/>.</para>
/// </summary>
public record ApproveProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    string ActorUserId) : ICommand<Result<RuntimeProposalDto>>;

public class ApproveProposalCommandHandler
    : ICommandHandler<ApproveProposalCommand, Result<RuntimeProposalDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly ICurrentExpandedSpecResolver _currentExpandedResolver;
    private readonly ILogger<ApproveProposalCommandHandler> _logger;

    public ApproveProposalCommandHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IHubContext<AgentHub, IAgentClient> agentHub,
        ICurrentExpandedSpecResolver currentExpandedResolver,
        ILogger<ApproveProposalCommandHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _agentHub = agentHub;
        _currentExpandedResolver = currentExpandedResolver;
        _logger = logger;
    }

    public async Task<Result<RuntimeProposalDto>> Handle(
        ApproveProposalCommand request,
        CancellationToken cancellationToken)
    {
        // Project-scoped lookup — a proposal id from project A queried under
        // project B must not leak. Cross-tenant reads collapse into "not_found".
        var proposal = await _db.RuntimeProposals
            .FirstOrDefaultAsync(
                p => p.Id == request.ProposalId && p.ProjectId == request.ProjectId,
                cancellationToken);
        if (proposal is null)
        {
            return Result.Failure<RuntimeProposalDto>("not_found");
        }

        if (proposal.Status != RuntimeProposalStatus.Pending)
        {
            return Result.Failure<RuntimeProposalDto>("already_decided");
        }

        // Project-level spec write per the `project-level-runtime-spec` spec.
        // The proposal still anchors to its originating runtime (RuntimeId) —
        // that's where the SignalR delta is pushed — but the persisted spec
        // lives on Project so every runtime under this project picks it up
        // on its next bootstrap.
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == proposal.ProjectId, cancellationToken);
        if (project is null)
        {
            // Project soft-deleted between proposal-create and approve. The
            // proposal row still exists; surface as not_found so the UI can
            // show a useful message.
            return Result.Failure<RuntimeProposalDto>("not_found");
        }

        var nowUtc = DateTime.UtcNow;
        proposal.Status = RuntimeProposalStatus.Approved;
        proposal.AppliedSpec = proposal.ProposedSpec;
        proposal.DecidedBy = request.ActorUserId;
        proposal.DecidedAt = nowUtc;

        // V3 semantics: the approved V3 spec replaces the project's persisted
        // spec wholesale (source of truth). Bump-by-one on SpecVersion so
        // reactors can detect "is this newer than what I last applied?"
        // cheaply.
        //
        // For the daemon-bound delta we work in V2 (the daemon's wire format).
        // ExpandedSpec is the V2 the expander produced at propose-time; we
        // diff it against the project's PRIOR V2 expansion (held in the
        // last-approved proposal's ExpandedSpec, see ResolveCurrentExpandedAsync).
        // SpecDelta.Compute tolerates a null current — a fresh project with
        // no prior spec produces an "everything is new" delta.
        var currentExpandedJson = await _currentExpandedResolver.ResolveAsync(
            project.Id, proposal.Id, cancellationToken);
        var delta = SpecDelta.Compute(currentExpandedJson, proposal.ExpandedSpec);

        project.Spec = proposal.AppliedSpec;
        project.SpecVersion = project.SpecVersion + 1;

        await _db.SaveChangesAsync(cancellationToken);

        // Push to the daemon. An empty-delta payload (HasChanges=false) is
        // valid — the proposed spec equals the current; the daemon ack-only
        // no-ops it and the row still flips to Applied.
        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{proposal.RuntimeId}")
                .ApplyRuntimeSpecDelta(new ApplyRuntimeSpecDeltaPayload(
                    ProposalId: proposal.Id,
                    Delta: delta));
        }
        catch (Exception ex)
        {
            // Daemon push is best-effort here — the row is already Approved on
            // disk, and a missing daemon will pick up its config on reconnect.
            // We don't fail the user-facing request just because the daemon's
            // SignalR connection is flaky.
            _logger.LogWarning(ex,
                "ApproveProposal: ApplyRuntimeSpecDelta push failed for runtime {RuntimeId} (proposal {ProposalId}); proposal row is Approved.",
                proposal.RuntimeId, proposal.Id);
        }

        // Fan-out to the project group so other tabs see the status change
        // without a refetch.
        try
        {
            await _agentHub.Clients
                .Group($"project-{proposal.ProjectId}")
                .RuntimeProposalUpdated(new RuntimeProposalUpdatedPayload(
                    ProposalId: proposal.Id,
                    ProjectId: proposal.ProjectId,
                    RuntimeId: proposal.RuntimeId,
                    Status: proposal.Status,
                    AppliedSpec: proposal.AppliedSpec,
                    ErrorMessage: proposal.ErrorMessage,
                    DecidedAt: proposal.DecidedAt,
                    DecidedBy: proposal.DecidedBy));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ApproveProposal: RuntimeProposalUpdated broadcast failed for proposal {ProposalId} (project {ProjectId}); persistence is unaffected.",
                proposal.Id, proposal.ProjectId);
        }

        _logger.LogInformation(
            "RuntimeProposal {ProposalId} approved by {ActorUserId} for runtime {RuntimeId} (project {ProjectId}); delta pushed.",
            proposal.Id, request.ActorUserId, proposal.RuntimeId, proposal.ProjectId);

        return Result.Success(MapToDto(proposal));
    }

    internal static RuntimeProposalDto MapToDto(RuntimeProposal p) => new(
        Id: p.Id,
        ProjectId: p.ProjectId,
        RuntimeId: p.RuntimeId,
        Status: p.Status,
        ProposedSpec: p.ProposedSpec,
        AppliedSpec: p.AppliedSpec,
        Reason: p.Reason,
        DecidedBy: p.DecidedBy,
        DecidedAt: p.DecidedAt,
        ErrorMessage: p.ErrorMessage,
        CreatedAt: p.CreatedAt);
}
