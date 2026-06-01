using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default reconciler. Pulls the canonical repo list from GitHub via
/// <see cref="IGithubApiClient"/>, then applies the diff against our DB.
/// </summary>
public sealed class GithubRepositorySyncService : IGithubRepositorySyncService
{
    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _api;
    private readonly IGithubAppTokenService _tokens;

    public GithubRepositorySyncService(
        ApplicationDbContext db,
        IGithubApiClient api,
        IGithubAppTokenService tokens)
    {
        _db = db;
        _api = api;
        _tokens = tokens;
    }

    public async Task<RepositorySyncResult> SyncAsync(
        Guid githubInstallationId,
        long githubInstallationNumericId,
        CancellationToken ct = default)
    {
        // Touch the token cache up-front so a transport failure surfaces before we mutate any DB state.
        await _tokens.GetInstallationTokenAsync(githubInstallationNumericId, ct);

        var live = await _api.ListInstallationRepositoriesAsync(githubInstallationNumericId, ct);

        var existing = await _db.GithubRepositories
            .Where(r => r.GithubInstallationId == githubInstallationId)
            .ToListAsync(ct);

        var liveById = live.ToDictionary(r => r.Id);
        var existingById = existing.ToDictionary(r => r.GithubRepoId);

        var now = DateTime.UtcNow;
        var added = 0;
        var updated = 0;
        var removed = 0;

        foreach (var liveRepo in live)
        {
            if (existingById.TryGetValue(liveRepo.Id, out var row))
            {
                var changed = false;
                if (row.Name != liveRepo.Name) { row.Name = liveRepo.Name; changed = true; }
                if (row.FullName != liveRepo.FullName) { row.FullName = liveRepo.FullName; changed = true; }
                if (row.Owner != liveRepo.Owner.Login) { row.Owner = liveRepo.Owner.Login; changed = true; }
                if (row.Private != liveRepo.Private) { row.Private = liveRepo.Private; changed = true; }
                if (row.DefaultBranch != liveRepo.DefaultBranch) { row.DefaultBranch = liveRepo.DefaultBranch; changed = true; }
                row.LastSyncedAt = now;
                if (changed) updated++;
            }
            else
            {
                _db.GithubRepositories.Add(new GithubRepository
                {
                    Id = Guid.NewGuid(),
                    GithubInstallationId = githubInstallationId,
                    GithubRepoId = liveRepo.Id,
                    Owner = liveRepo.Owner.Login,
                    Name = liveRepo.Name,
                    FullName = liveRepo.FullName,
                    Private = liveRepo.Private,
                    DefaultBranch = liveRepo.DefaultBranch,
                    LastSyncedAt = now,
                });
                added++;
            }
        }

        foreach (var stale in existing.Where(r => !liveById.ContainsKey(r.GithubRepoId)))
        {
            _db.GithubRepositories.Remove(stale);
            removed++;
        }

        await _db.SaveChangesAsync(ct);

        return new RepositorySyncResult(added, updated, removed, live.Count);
    }

    public async Task UpsertFromWebhookAsync(
        Guid githubInstallationId,
        IEnumerable<GithubWebhookRepoDto> repos,
        CancellationToken ct = default)
    {
        var list = repos as IReadOnlyCollection<GithubWebhookRepoDto> ?? repos.ToList();
        if (list.Count == 0) return;

        var ids = list.Select(r => r.Id).ToHashSet();
        var existing = await _db.GithubRepositories
            .Where(r => r.GithubInstallationId == githubInstallationId && ids.Contains(r.GithubRepoId))
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(r => r.GithubRepoId);

        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var dto in list)
        {
            // GitHub's webhook gives us "owner/name" but no separate owner field.
            // Derive owner from full_name; if the slash is missing (shouldn't happen for real
            // GitHub payloads), fall back to whatever we have.
            var slash = dto.FullName.IndexOf('/');
            var owner = slash > 0 ? dto.FullName[..slash] : string.Empty;

            if (existingById.TryGetValue(dto.Id, out var row))
            {
                if (row.Name != dto.Name) { row.Name = dto.Name; changed = true; }
                if (row.FullName != dto.FullName) { row.FullName = dto.FullName; changed = true; }
                if (!string.IsNullOrEmpty(owner) && row.Owner != owner) { row.Owner = owner; changed = true; }
                if (row.Private != dto.Private) { row.Private = dto.Private; changed = true; }
            }
            else
            {
                _db.GithubRepositories.Add(new GithubRepository
                {
                    Id = Guid.NewGuid(),
                    GithubInstallationId = githubInstallationId,
                    GithubRepoId = dto.Id,
                    Owner = owner,
                    Name = dto.Name,
                    FullName = dto.FullName,
                    Private = dto.Private,
                    DefaultBranch = null, // not in the webhook payload — first manual/REST sync fills this in.
                    LastSyncedAt = now,
                });
                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveByGithubRepoIdsAsync(
        Guid githubInstallationId,
        IEnumerable<long> githubRepoIds,
        CancellationToken ct = default)
    {
        var ids = githubRepoIds as IReadOnlyCollection<long> ?? githubRepoIds.ToList();
        if (ids.Count == 0) return;

        var rows = await _db.GithubRepositories
            .Where(r => r.GithubInstallationId == githubInstallationId && ids.Contains(r.GithubRepoId))
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        _db.GithubRepositories.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
    }
}
