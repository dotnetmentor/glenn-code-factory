using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// Daemon- or user-initiated proposal: the in-runtime daemon's
/// <c>propose_runtime_spec</c> tool calls
/// <c>POST /api/runtimes/{runtimeId}/proposals</c>; the user-side spec editor
/// posts to <c>POST /api/projects/{projectId}/proposals</c>. Both land here.
/// Persists a <see cref="RuntimeProposal"/> in
/// <see cref="RuntimeProposalStatus.Pending"/> and fans out a
/// <see cref="RuntimeProposalCreatedPayload"/> to the <c>project-{ProjectId}</c>
/// SignalR group so the user sees a confirmation card in the chat panel.
///
/// <para><b>V3 spec.</b> The proposed body is a <see cref="RuntimeSpecV3"/> —
/// preset-based: each service picks a preset slug and supplies its parameter
/// values. Structural invariants (version stamp, non-empty unique service
/// names, kind/name presence) are enforced by
/// <see cref="RuntimeSpecV3.Validate"/>; preset existence + parameter
/// type-checking happen inside
/// <see cref="IPresetExpander.ExpandAsync"/> on the same call. The expander
/// produces a daemon-bound <c>RuntimeSpecV2</c> which we persist alongside
/// the V3 source-of-truth on the proposal row — that way approval doesn't
/// have to re-expand (and won't see drift if a preset is edited between
/// propose and approve).</para>
///
/// <para><b>SignalR payload is V3.</b> The browser-facing card renders V3
/// because that's the higher-fidelity source-of-truth shape — the user is
/// reviewing what the agent (or operator) authored, not the expanded V2.</para>
///
/// <para>The daemon never installs anything itself — the approve→apply path
/// (<see cref="ApproveProposalCommand"/> / <see cref="EditProposalCommand"/>)
/// pushes a <c>RuntimeSpecDeltaV2</c> back to the daemon, computed against
/// the persisted V2 expansion. This command is "create the proposal + tell
/// the UI"; the user-action arms (Approve / Edit / Reject) live in sibling
/// handlers.</para>
/// </summary>
public record CreateRuntimeProposalCommand(
    Guid RuntimeId,
    RuntimeSpecV3 ProposedSpec,
    string Reason) : ICommand<Result<CreateRuntimeProposalResponse>>;

public record CreateRuntimeProposalResponse(Guid ProposalId);

public class CreateRuntimeProposalCommandHandler
    : ICommandHandler<CreateRuntimeProposalCommand, Result<CreateRuntimeProposalResponse>>
{
    private readonly ApplicationDbContext _db;
    // RuntimeProposalCreated is on IAgentClient (browser-facing); the hub that
    // owns IAgentClient connections is AgentHub. The daemon-facing RuntimeHub
    // implements Hub<IRuntimeClient> and can't be used to send IAgentClient
    // messages. Mirrors BroadcastRuntimeStateChangedHandler / the project-group
    // broadcasters in RuntimeHub itself, which inject IHubContext<AgentHub, IAgentClient>.
    private readonly IHubContext<AgentHub, IAgentClient> _hub;
    private readonly IPresetExpander _expander;
    // Used to drive the EXISTING approve+apply path (ApproveProposalCommand) when
    // repair consent is armed — we do NOT fork the delta/apply machinery.
    private readonly IMediator _mediator;
    private readonly ILogger<CreateRuntimeProposalCommandHandler> _logger;

    public CreateRuntimeProposalCommandHandler(
        ApplicationDbContext db,
        IHubContext<AgentHub, IAgentClient> hub,
        IPresetExpander expander,
        IMediator mediator,
        ILogger<CreateRuntimeProposalCommandHandler> logger)
    {
        _db = db;
        _hub = hub;
        _expander = expander;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<CreateRuntimeProposalResponse>> Handle(
        CreateRuntimeProposalCommand request,
        CancellationToken cancellationToken)
    {
        // Soft-deleted runtimes are filtered by the global query filter — a
        // torn-down runtime can't accept new proposals. Mirrors
        // BootstrapMcpConfigController and friends.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == request.RuntimeId, cancellationToken);
        if (runtime is null)
        {
            return Result.Failure<CreateRuntimeProposalResponse>("not_found");
        }

        // Structural V3 validation — version stamp, services non-empty,
        // unique kind/name pairs. Cheap, pure, doesn't need the DB.
        var validate = request.ProposedSpec.Validate();
        if (validate.IsFailure)
        {
            return Result.Failure<CreateRuntimeProposalResponse>(validate.Error!);
        }

        // Expand the V3 into the daemon-bound V2 wire shape. This is the call
        // that hits the DB (loads referenced presets, renders handlebars,
        // type-checks parameters). Failure here surfaces as a stable
        // snake_case error code from PresetExpander — see its docs for the
        // catalog (preset_not_found, param_required, param_not_integer, etc.).
        var expansion = await _expander.ExpandAsync(request.ProposedSpec, cancellationToken);
        if (expansion.IsFailure)
        {
            return Result.Failure<CreateRuntimeProposalResponse>(expansion.Error!);
        }

        var proposedSpecJson = request.ProposedSpec.ToJson();
        var expandedSpecJson = expansion.Value.ToJson();

        var proposal = new RuntimeProposal
        {
            Id = Guid.NewGuid(),
            ProjectId = runtime.ProjectId,
            RuntimeId = runtime.Id,
            Status = RuntimeProposalStatus.Pending,
            ProposedSpec = proposedSpecJson,
            ExpandedSpec = expandedSpecJson,
            Reason = request.Reason,
        };
        _db.RuntimeProposals.Add(proposal);
        await _db.SaveChangesAsync(cancellationToken);

        // Fan out to the project group so every connected browser tab sees
        // the confirmation card. Failures are NOT caught here — the proposal
        // row is the source of truth, but this command's job is "create + notify"
        // and a failed notification leaves the UI without a card to act on,
        // which is worse than a 500 to the daemon (the daemon can retry the
        // tool call). The payload carries V3 (the source-of-truth shape the
        // super-admin reviewer cares about); the expanded V2 stays server-side
        // until approve-time.
        var payload = new RuntimeProposalCreatedPayload(
            ProposalId: proposal.Id,
            RuntimeId: runtime.Id,
            ProjectId: runtime.ProjectId,
            ProposedSpec: proposedSpecJson,
            Reason: request.Reason);
        await _hub.Clients
            .Group($"project-{runtime.ProjectId}")
            .RuntimeProposalCreated(payload);

        _logger.LogInformation(
            "RuntimeProposal {ProposalId} created for runtime {RuntimeId} (project {ProjectId}); fanned out to project group.",
            proposal.Id, runtime.Id, runtime.ProjectId);

        // -------- Budgeted auto-apply consent (self-healing-runtime-specs, B2/B3) --------
        //
        // When the operator clicked "Let agent fix it", RepairRuntimeCommand armed
        // budgeted consent on the runtime row. A single repair turn can produce
        // multiple propose→apply→fail→correct cycles, so the consent SURVIVES a
        // failed apply (bounded by the budget counter + 30-min window) — the
        // corrected retry must still auto-apply. The gate requires ALL of:
        //   - AutoApplyNextProposal == true
        //   - AutoApplyExpiresAt > now (window not elapsed)
        //   - AutoApplyAttemptsRemaining > 0 (budget left)
        //
        // On a passing gate we DECREMENT the budget; we clear the flag + expiry
        // ONLY when the budget hits 0 (clearing while budget remains would strand
        // the corrected retry needing a manual click — the exact bug B3 fixes).
        // Then we run the EXISTING approve+apply path via ApproveProposalCommand
        // (which pushes the V2 delta to the daemon) — we do NOT fork the delta /
        // apply machinery. The actual apply outcome lands async in
        // RecordApplyResultCommand: SUCCESS clears all consent (healed), FAILURE
        // leaves it intact so the next corrected proposal can auto-apply.
        var consentArmed = runtime.AutoApplyNextProposal
            && runtime.AutoApplyExpiresAt is { } expiresAt
            && expiresAt > DateTime.UtcNow
            && runtime.AutoApplyAttemptsRemaining > 0;

        if (consentArmed)
        {
            runtime.AutoApplyAttemptsRemaining -= 1;
            if (runtime.AutoApplyAttemptsRemaining <= 0)
            {
                // Budget exhausted — disarm. (A subsequent successful apply would
                // also clear it, but the corrected retry can't auto-apply past 0.)
                runtime.AutoApplyNextProposal = false;
                runtime.AutoApplyExpiresAt = null;
                runtime.AutoApplyAttemptsRemaining = 0;
            }
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "RuntimeProposal {ProposalId}: repair consent armed — auto-applying via approve path (budget now {Budget}, runtime {RuntimeId}).",
                proposal.Id, runtime.AutoApplyAttemptsRemaining, runtime.Id);

            // Reuse the EXISTING approve+apply machinery. Auto-applies are
            // attributed to a stable system actor so the audit row records that
            // this was applied via repair consent rather than a human click.
            var approve = await _mediator.Send(
                new ApproveProposalCommand(
                    ProjectId: runtime.ProjectId,
                    ProposalId: proposal.Id,
                    ActorUserId: AutoApplyActorUserId),
                cancellationToken);

            if (approve.IsFailure)
            {
                // The proposal row is already persisted + broadcast; a failed
                // auto-approve (e.g. project soft-deleted between create + approve)
                // is logged but does not sink the create — the proposal stays
                // Pending and is resolvable manually. The consent decrement stands.
                _logger.LogWarning(
                    "RuntimeProposal {ProposalId}: auto-apply approve failed ({Error}); proposal left Pending for manual resolution.",
                    proposal.Id, approve.Error);
            }
        }

        return Result.Success(new CreateRuntimeProposalResponse(proposal.Id));
    }

    /// <summary>
    /// Stable system actor stamped on <c>DecidedBy</c> when a proposal is
    /// auto-applied via armed repair consent — distinguishes a self-heal
    /// auto-apply from a human Approve/Edit click in the audit trail.
    /// </summary>
    public const string AutoApplyActorUserId = "system:repair-auto-apply";
}
