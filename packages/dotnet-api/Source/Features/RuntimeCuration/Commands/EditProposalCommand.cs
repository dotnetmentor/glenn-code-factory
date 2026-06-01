using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// User-initiated decision on a pending <see cref="RuntimeProposal"/>: ship a
/// user-edited <see cref="RuntimeSpecV3"/> body in place of the daemon's
/// original. Validates the edited spec via
/// <see cref="RuntimeSpecV3.Validate"/>, re-expands it through
/// <see cref="IPresetExpander"/> to refresh the persisted V2 wire shape (so
/// the daemon-bound delta uses the user's edits, not the propose-time
/// expansion), persists both the V3 (<c>AppliedSpec</c>) and the refreshed V2
/// (<c>ExpandedSpec</c>), replaces the <b>project</b>'s spec with the V3, and
/// pushes a V2 delta to the proposal's originating runtime. Same flow as
/// <see cref="ApproveProposalCommand"/> from step 5 onward.
/// </summary>
public record EditProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    RuntimeSpecV3 EditedSpec,
    string ActorUserId) : ICommand<Result<RuntimeProposalDto>>;

public class EditProposalCommandHandler
    : ICommandHandler<EditProposalCommand, Result<RuntimeProposalDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly IPresetExpander _expander;
    private readonly ILogger<EditProposalCommandHandler> _logger;

    public EditProposalCommandHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IHubContext<AgentHub, IAgentClient> agentHub,
        IPresetExpander expander,
        ILogger<EditProposalCommandHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _agentHub = agentHub;
        _expander = expander;
        _logger = logger;
    }

    public async Task<Result<RuntimeProposalDto>> Handle(
        EditProposalCommand request,
        CancellationToken cancellationToken)
    {
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
        // lives on Project.
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == proposal.ProjectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<RuntimeProposalDto>("not_found");
        }

        // Structural V3 validation — version stamp, services non-empty,
        // unique kind/name pairs.
        var validate = request.EditedSpec.Validate();
        if (validate.IsFailure)
        {
            return Result.Failure<RuntimeProposalDto>(validate.Error!);
        }

        // Re-expand against the live preset registry — the user may have
        // edited values that change which presets are referenced, so we MUST
        // produce a fresh V2 rather than reusing proposal.ExpandedSpec.
        var expansion = await _expander.ExpandAsync(request.EditedSpec, cancellationToken);
        if (expansion.IsFailure)
        {
            return Result.Failure<RuntimeProposalDto>(expansion.Error!);
        }

        var editedSpecJson = request.EditedSpec.ToJson();
        var refreshedExpandedJson = expansion.Value.ToJson();

        var nowUtc = DateTime.UtcNow;
        proposal.Status = RuntimeProposalStatus.Edited;
        proposal.AppliedSpec = editedSpecJson;
        proposal.ExpandedSpec = refreshedExpandedJson;
        proposal.DecidedBy = request.ActorUserId;
        proposal.DecidedAt = nowUtc;

        // V3 stays the project's source of truth; the daemon-bound delta is
        // computed in V2 against the project's prior V2 expansion (resolved
        // from the most-recent previously-applied proposal).
        var currentExpandedJson = await ResolveCurrentExpandedAsync(
            project.Id, proposal.Id, cancellationToken);
        var delta = SpecDelta.Compute(currentExpandedJson, refreshedExpandedJson);

        project.Spec = editedSpecJson;
        project.SpecVersion = project.SpecVersion + 1;

        await _db.SaveChangesAsync(cancellationToken);

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
            _logger.LogWarning(ex,
                "EditProposal: ApplyRuntimeSpecDelta push failed for runtime {RuntimeId} (proposal {ProposalId}); proposal row is Edited.",
                proposal.RuntimeId, proposal.Id);
        }

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
                "EditProposal: RuntimeProposalUpdated broadcast failed for proposal {ProposalId} (project {ProjectId}); persistence is unaffected.",
                proposal.Id, proposal.ProjectId);
        }

        _logger.LogInformation(
            "RuntimeProposal {ProposalId} edited by {ActorUserId} for runtime {RuntimeId} (project {ProjectId}); delta pushed.",
            proposal.Id, request.ActorUserId, proposal.RuntimeId, proposal.ProjectId);

        return Result.Success(ApproveProposalCommandHandler.MapToDto(proposal));
    }

    /// <summary>
    /// Locate the project's CURRENT V2 expansion to diff against — same lookup
    /// shape as <see cref="ApproveProposalCommandHandler"/>. See its docs.
    /// </summary>
    private async Task<string?> ResolveCurrentExpandedAsync(
        Guid projectId,
        Guid currentProposalId,
        CancellationToken ct)
    {
        var lastApplied = await _db.RuntimeProposals
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId
                        && p.Id != currentProposalId
                        && p.ExpandedSpec != null
                        && (p.Status == RuntimeProposalStatus.Approved
                            || p.Status == RuntimeProposalStatus.Edited
                            || p.Status == RuntimeProposalStatus.Applied))
            .OrderByDescending(p => p.DecidedAt)
            .Select(p => p.ExpandedSpec)
            .FirstOrDefaultAsync(ct);
        return lastApplied;
    }
}
