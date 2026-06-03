using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// Project-wide rollup of missing required env vars across all (non-archived)
/// branches. <b>Required</b> is resolved from the project's current expanded
/// spec — the same source as <see cref="GetBranchEnvStatusQuery"/> — and is
/// identical for every branch (the resolver is project-scoped). Only
/// <b>present</b> differs per branch, because a branch override can supply a key
/// the project default lacks.
///
/// <para>Drives the cross-branch env indicators (sidebar branch dots, the
/// settings-trigger badge) from a single round-trip instead of fanning out one
/// <see cref="GetBranchEnvStatusQuery"/> per branch.</para>
/// </summary>
public record GetProjectEnvStatusSummaryQuery(Guid ProjectId)
    : IQuery<Result<ProjectEnvStatusSummaryResponse>>;

/// <summary>Per-branch missing-required-var rollup entry.</summary>
public record BranchEnvMissingSummary(
    Guid BranchId,
    string BranchName,
    int MissingCount,
    string[] MissingKeys);

/// <summary>
/// Result of <see cref="GetProjectEnvStatusSummaryQuery"/>: how many required
/// vars the project declares, how many branches have at least one missing, and
/// the per-branch breakdown (sorted by branch name for deterministic output).
/// </summary>
public record ProjectEnvStatusSummaryResponse(
    int RequiredCount,
    int BranchesWithMissing,
    BranchEnvMissingSummary[] Branches);

public class GetProjectEnvStatusSummaryQueryHandler
    : IQueryHandler<GetProjectEnvStatusSummaryQuery, Result<ProjectEnvStatusSummaryResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentExpandedSpecResolver _currentExpandedResolver;

    public GetProjectEnvStatusSummaryQueryHandler(
        ApplicationDbContext db,
        ICurrentExpandedSpecResolver currentExpandedResolver)
    {
        _db = db;
        _currentExpandedResolver = currentExpandedResolver;
    }

    public async Task<Result<ProjectEnvStatusSummaryResponse>> Handle(
        GetProjectEnvStatusSummaryQuery request,
        CancellationToken cancellationToken)
    {
        // -------- required keys (project-level, identical across branches) ------
        var requiredKeys = await ResolveRequiredKeysAsync(request.ProjectId, cancellationToken);

        // No required vars → nothing can be missing anywhere. Short-circuit
        // before touching secrets / branches.
        if (requiredKeys.Count == 0)
        {
            return Result.Success(new ProjectEnvStatusSummaryResponse(0, 0, []));
        }

        // -------- present keys, split by scope --------
        var rows = await _db.ProjectSecrets
            .AsNoTracking()
            .Where(s => s.ProjectId == request.ProjectId)
            .Select(s => new { s.Key, s.BranchId })
            .ToListAsync(cancellationToken);

        var projectWideKeys = new HashSet<string>(
            rows.Where(r => r.BranchId == null).Select(r => r.Key),
            StringComparer.Ordinal);

        var branchOverrideKeys = rows
            .Where(r => r.BranchId != null)
            .GroupBy(r => r.BranchId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.Select(r => r.Key), StringComparer.Ordinal));

        // -------- per-branch missing --------
        var branches = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == request.ProjectId && !b.IsArchived)
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(cancellationToken);

        var summaries = new List<BranchEnvMissingSummary>(branches.Count);
        foreach (var branch in branches)
        {
            branchOverrideKeys.TryGetValue(branch.Id, out var overrides);
            var missingKeys = requiredKeys
                .Where(k => !projectWideKeys.Contains(k)
                    && (overrides is null || !overrides.Contains(k)))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            summaries.Add(new BranchEnvMissingSummary(
                branch.Id, branch.Name, missingKeys.Length, missingKeys));
        }

        summaries.Sort((a, b) =>
            string.Compare(a.BranchName, b.BranchName, StringComparison.Ordinal));
        var branchesWithMissing = summaries.Count(s => s.MissingCount > 0);

        return Result.Success(new ProjectEnvStatusSummaryResponse(
            requiredKeys.Count, branchesWithMissing, summaries.ToArray()));
    }

    /// <summary>
    /// Required env keys = union of <see cref="ServiceSpec.RequiredEnv"/> (where
    /// <c>IsRequired</c>) across the project's current expanded spec. Mirrors the
    /// required-set derivation in <see cref="GetBranchEnvStatusQuery"/>.
    /// </summary>
    private async Task<HashSet<string>> ResolveRequiredKeysAsync(
        Guid projectId,
        CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var currentExpandedJson = await _currentExpandedResolver.ResolveAsync(
            projectId, excludeProposalId: null, ct);
        if (string.IsNullOrWhiteSpace(currentExpandedJson))
        {
            return keys;
        }

        var parsed = RuntimeSpecV2.TryParse(currentExpandedJson);
        if (!parsed.IsSuccess || parsed.Value.Services is not { Count: > 0 } services)
        {
            return keys;
        }

        foreach (var service in services)
        {
            if (service.RequiredEnv is not { Count: > 0 } reqs)
            {
                continue;
            }

            foreach (var req in reqs)
            {
                if (!string.IsNullOrEmpty(req.Key) && req.IsRequired)
                {
                    keys.Add(req.Key);
                }
            }
        }

        return keys;
    }
}
