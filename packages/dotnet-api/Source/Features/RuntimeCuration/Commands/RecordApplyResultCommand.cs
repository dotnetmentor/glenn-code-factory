using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeEvents.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// Daemon-initiated ack of an <see cref="ApplyRuntimeSpecDeltaPayload"/> push.
/// Routed via <see cref="SignalR.Hubs.RuntimeHub.RuntimeSpecDeltaApplied"/> after
/// the daemon's RuntimeToken-authenticated hub call has projected its
/// <c>rt_runtime</c> + <c>rt_project</c> claims to <see cref="RuntimeId"/> +
/// <see cref="ProjectId"/>.
///
/// <list type="bullet">
///   <item>Idempotent on a missing proposal — daemons may retry the ack across
///         a server restart that lost in-memory state. Log + Success.</item>
///   <item>Cross-runtime / cross-project mismatches log a warning and return
///         Success — defensive against a stale daemon ack; we don't crash the
///         hub on bad input.</item>
///   <item>Success ack → Status=Applied, ErrorMessage cleared.</item>
///   <item>Failure ack → Status=Failed, ErrorMessage carries the daemon's
///         stderr / explanation.</item>
/// </list>
/// </summary>
public record RecordApplyResultCommand(
    Guid RuntimeId,
    Guid ProjectId,
    RuntimeSpecDeltaApplyResultPayload Payload) : ICommand<Result<Unit>>;

public class RecordApplyResultCommandHandler
    : ICommandHandler<RecordApplyResultCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly ILogger<RecordApplyResultCommandHandler> _logger;

    public RecordApplyResultCommandHandler(
        ApplicationDbContext db,
        IHubContext<AgentHub, IAgentClient> agentHub,
        ILogger<RecordApplyResultCommandHandler> logger)
    {
        _db = db;
        _agentHub = agentHub;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(
        RecordApplyResultCommand request,
        CancellationToken cancellationToken)
    {
        var proposal = await _db.RuntimeProposals
            .FirstOrDefaultAsync(p => p.Id == request.Payload.ProposalId, cancellationToken);
        if (proposal is null)
        {
            // Idempotent: daemon may be replaying after a server restart that
            // lost the proposal row, or a soft-delete swept it. Don't crash the
            // hub on a stale ack.
            _logger.LogWarning(
                "RecordApplyResult: proposal {ProposalId} not found; daemon ack treated as no-op.",
                request.Payload.ProposalId);
            return Result.Success(Unit.Value);
        }

        // Cross-check the daemon's claimed runtime/project against the proposal
        // row — defense-in-depth against a stale ack from a redeployed runtime
        // claiming a peer's proposal id. Log + success rather than fail loudly:
        // the daemon owns the retry semantics, and a hard fail would crash the
        // hub method on a hot path.
        if (proposal.RuntimeId != request.RuntimeId || proposal.ProjectId != request.ProjectId)
        {
            _logger.LogWarning(
                "RecordApplyResult: proposal {ProposalId} mismatch — proposal.RuntimeId={ProposalRuntime}, claim.RuntimeId={ClaimRuntime}, proposal.ProjectId={ProposalProject}, claim.ProjectId={ClaimProject}; treating as no-op.",
                proposal.Id, proposal.RuntimeId, request.RuntimeId, proposal.ProjectId, request.ProjectId);
            return Result.Success(Unit.Value);
        }

        if (request.Payload.Success)
        {
            proposal.Status = RuntimeProposalStatus.Applied;
            proposal.ErrorMessage = null;

            // Budgeted auto-apply consent (self-healing-runtime-specs, B3): ONE
            // successful apply = the runtime is healed, so stop auto-applying —
            // clear ALL consent on the runtime row regardless of remaining budget
            // or window. Best-effort: the runtime row may be soft-deleted between
            // propose and ack, in which case there's simply nothing to clear. The
            // FAILURE branch deliberately leaves consent UNTOUCHED so the agent's
            // corrected retry can still auto-apply within the budget — that's the
            // whole point of budgeted consent.
            var runtime = await _db.ProjectRuntimes
                .FirstOrDefaultAsync(r => r.Id == request.RuntimeId, cancellationToken);
            if (runtime is not null
                && (runtime.AutoApplyNextProposal
                    || runtime.AutoApplyAttemptsRemaining != 0
                    || runtime.AutoApplyExpiresAt is not null))
            {
                runtime.AutoApplyNextProposal = false;
                runtime.AutoApplyExpiresAt = null;
                runtime.AutoApplyAttemptsRemaining = 0;
                _logger.LogInformation(
                    "RecordApplyResult: proposal {ProposalId} applied successfully — repair consent cleared on runtime {RuntimeId} (healed).",
                    proposal.Id, request.RuntimeId);
            }
        }
        else
        {
            proposal.Status = RuntimeProposalStatus.Failed;
            proposal.ErrorMessage = request.Payload.Error;
            // FAILURE: leave repair consent UNTOUCHED — the corrected retry must
            // still be able to auto-apply within the remaining budget + window.
        }

        // Persist apply-timing data so the Apply History surface survives the
        // rolling 5000-event cap on RuntimeEvent. TotalApplyMs is approximated
        // as the elapsed time between DecidedAt (when Approve/Edit stamped the
        // row and the API pushed ApplyRuntimeSpecDelta to the daemon) and now.
        // PhaseTimings is pulled best-effort from the matching SpecDelta*
        // RuntimeEvent row's payload (same source as the inline extraction in
        // GetRuntimeApplyHistoryQuery); null when the event row isn't present
        // (early ack, event-dropped, or daemon emitted apply-result-only).
        if (proposal.DecidedAt is not null)
        {
            var elapsed = DateTime.UtcNow - proposal.DecidedAt.Value;
            // Clamp to non-negative — if the daemon's clock ran ahead, we still
            // want a sane stored value.
            proposal.TotalApplyMs = (long)Math.Max(0, elapsed.TotalMilliseconds);
        }
        proposal.PhaseTimings = await TryExtractPhaseTimingsAsync(
            proposal.RuntimeId, proposal.Id, proposal.DecidedAt, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

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
                "RecordApplyResult: RuntimeProposalUpdated broadcast failed for proposal {ProposalId} (project {ProjectId}); persistence is unaffected.",
                proposal.Id, proposal.ProjectId);
        }

        _logger.LogInformation(
            "RecordApplyResult: proposal {ProposalId} → {Status} (runtime {RuntimeId}, project {ProjectId}, totalApplyMs={TotalApplyMs}, hasPhaseTimings={HasPhaseTimings}).",
            proposal.Id, proposal.Status, proposal.RuntimeId, proposal.ProjectId,
            proposal.TotalApplyMs, proposal.PhaseTimings != null);

        return Result.Success(Unit.Value);
    }

    /// <summary>
    /// Best-effort lookup of the matching <c>SpecDeltaApplied</c> /
    /// <c>SpecDeltaFailed</c> <see cref="RuntimeEvent"/> for this proposal and
    /// extraction of its <c>phaseTimings</c> sub-object as a raw JSON string.
    /// Mirrors the in-line extraction in <c>GetRuntimeApplyHistoryQuery</c>
    /// so the persisted column matches what the live query has been
    /// returning. Returns <c>null</c> when:
    /// <list type="bullet">
    ///   <item>no matching event row exists yet (daemon ack arrived first);</item>
    ///   <item>the payload doesn't include a <c>phaseTimings</c> field;</item>
    ///   <item>the payload is unparseable JSON.</item>
    /// </list>
    /// We narrow the search window to events recent to <see cref="RuntimeProposal.DecidedAt"/>
    /// (with a 60-second forgiving margin on either side, same as the query)
    /// to keep the scan cheap and avoid picking up an unrelated apply event
    /// for the same runtime.
    /// </summary>
    private async Task<string?> TryExtractPhaseTimingsAsync(
        Guid runtimeId,
        Guid proposalId,
        DateTime? decidedAt,
        CancellationToken ct)
    {
        try
        {
            var cutoff = (decidedAt ?? DateTime.UtcNow.AddMinutes(-30)).AddSeconds(-60);
            var candidates = await _db.RuntimeEvents
                .AsNoTracking()
                .Where(e => e.RuntimeId == runtimeId
                         && e.Timestamp >= cutoff
                         && (e.Type == RuntimeEventTypes.SpecDeltaApplied
                          || e.Type == RuntimeEventTypes.SpecDeltaFailed))
                .Select(e => new { e.Payload })
                .ToListAsync(ct);

            foreach (var ev in candidates)
            {
                if (string.IsNullOrWhiteSpace(ev.Payload)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(ev.Payload);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                    // Match the daemon's camelCase first, tolerate PascalCase.
                    if ((doc.RootElement.TryGetProperty("proposalId", out var pid)
                         || doc.RootElement.TryGetProperty("ProposalId", out pid))
                        && pid.ValueKind == JsonValueKind.String
                        && Guid.TryParse(pid.GetString(), out var parsedId)
                        && parsedId == proposalId)
                    {
                        if (doc.RootElement.TryGetProperty("phaseTimings", out var pt)
                            || doc.RootElement.TryGetProperty("PhaseTimings", out pt))
                        {
                            return pt.GetRawText();
                        }
                        return null;
                    }
                }
                catch (JsonException)
                {
                    // Malformed event payload — skip and keep looking.
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort observability lookup — must never break the ack
            // persistence path. Log and continue with null.
            _logger.LogDebug(ex,
                "RecordApplyResult: failed to extract phaseTimings for proposal {ProposalId} (runtime {RuntimeId}); persisting null.",
                proposalId, runtimeId);
        }
        return null;
    }
}
