using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeCuration.Services;

/// <summary>
/// Resolves a project's CURRENT daemon-bound V2 expanded spec — the V2 the
/// daemon last saw / installed. Source of truth is the most-recent
/// <see cref="RuntimeProposal"/> for the project that has an
/// <see cref="RuntimeProposal.ExpandedSpec"/> populated and reached one of the
/// terminal-write statuses (<see cref="RuntimeProposalStatus.Approved"/> /
/// <see cref="RuntimeProposalStatus.Edited"/> / <see cref="RuntimeProposalStatus.Applied"/>),
/// ordered by <see cref="RuntimeProposal.DecidedAt"/> descending.
///
/// <para><b>Why a shared service.</b> This resolution path was originally a
/// private helper on <c>ApproveProposalCommandHandler.ResolveCurrentExpandedAsync</c>.
/// The branch-level env-status query needs the SAME notion of "what's actually
/// deployed" so its missing-required calculation matches the live spec. Rather
/// than replicate the LINQ (and risk it drifting subtly), the logic lives here
/// once and both callers share it.</para>
///
/// <para><b>Scope is project-level.</b> The runtime spec is stored on
/// <c>Project.Spec</c> (V3 source of truth) and expanded to V2 on the proposal
/// row — there is one spec per project that every branch inherits. So the
/// "current expanded spec" is resolved per-project, not per-branch; a branch's
/// status reflects the project-wide spec it runs under.</para>
/// </summary>
public interface ICurrentExpandedSpecResolver
{
    /// <summary>
    /// Return the project's current V2 expanded spec JSON, or <c>null</c> when
    /// the project has no terminal-write proposal yet (fresh project — callers
    /// treat null as "no prior state" / "no required env declared").
    /// </summary>
    /// <param name="projectId">The project to resolve the current spec for.</param>
    /// <param name="excludeProposalId">
    /// Optional proposal id to exclude from consideration. Used by the approval
    /// flow to skip the proposal currently being approved (its ExpandedSpec is
    /// the PROPOSED state, not the current). Pass <c>null</c> to consider all
    /// terminal-write proposals (the read-only / status path).
    /// </param>
    Task<string?> ResolveAsync(
        Guid projectId,
        Guid? excludeProposalId,
        CancellationToken ct);
}

/// <inheritdoc />
public class CurrentExpandedSpecResolver : ICurrentExpandedSpecResolver
{
    private readonly ApplicationDbContext _db;

    public CurrentExpandedSpecResolver(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<string?> ResolveAsync(
        Guid projectId,
        Guid? excludeProposalId,
        CancellationToken ct)
    {
        return await _db.RuntimeProposals
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId
                        && (excludeProposalId == null || p.Id != excludeProposalId)
                        && p.ExpandedSpec != null
                        && (p.Status == RuntimeProposalStatus.Approved
                            || p.Status == RuntimeProposalStatus.Edited
                            || p.Status == RuntimeProposalStatus.Applied))
            .OrderByDescending(p => p.DecidedAt)
            .Select(p => p.ExpandedSpec)
            .FirstOrDefaultAsync(ct);
    }
}
