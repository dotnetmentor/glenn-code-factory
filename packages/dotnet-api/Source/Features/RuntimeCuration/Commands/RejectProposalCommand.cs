using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// User-initiated decision on a pending <see cref="RuntimeProposal"/>: dismiss
/// the proposal without changing the runtime. No delta is computed; nothing is
/// pushed to the daemon. Only the project group hears about the status change
/// so the chat panel can fade out the confirmation card.
///
/// <list type="number">
///   <item>Load scoped to <see cref="ProjectId"/>; not found / cross-project → <c>not_found</c>.</item>
///   <item>Status guard: must be <see cref="RuntimeProposalStatus.Pending"/> → otherwise <c>already_decided</c>.</item>
///   <item>Stamp <c>Status=Rejected</c>, <c>DecidedBy/At</c>. <c>AppliedSpec</c> stays null.</item>
///   <item>Broadcast <see cref="IAgentClient.RuntimeProposalUpdated"/>.</item>
/// </list>
/// </summary>
public record RejectProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    string ActorUserId) : ICommand<Result<RuntimeProposalDto>>;

public class RejectProposalCommandHandler
    : ICommandHandler<RejectProposalCommand, Result<RuntimeProposalDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly ILogger<RejectProposalCommandHandler> _logger;

    public RejectProposalCommandHandler(
        ApplicationDbContext db,
        IHubContext<AgentHub, IAgentClient> agentHub,
        ILogger<RejectProposalCommandHandler> logger)
    {
        _db = db;
        _agentHub = agentHub;
        _logger = logger;
    }

    public async Task<Result<RuntimeProposalDto>> Handle(
        RejectProposalCommand request,
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

        var nowUtc = DateTime.UtcNow;
        proposal.Status = RuntimeProposalStatus.Rejected;
        proposal.DecidedBy = request.ActorUserId;
        proposal.DecidedAt = nowUtc;

        await _db.SaveChangesAsync(cancellationToken);

        // Project-group fan-out only — runtime is untouched, no daemon push.
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
                "RejectProposal: RuntimeProposalUpdated broadcast failed for proposal {ProposalId} (project {ProjectId}); persistence is unaffected.",
                proposal.Id, proposal.ProjectId);
        }

        _logger.LogInformation(
            "RuntimeProposal {ProposalId} rejected by {ActorUserId} (project {ProjectId}).",
            proposal.Id, request.ActorUserId, proposal.ProjectId);

        return Result.Success(ApproveProposalCommandHandler.MapToDto(proposal));
    }
}
