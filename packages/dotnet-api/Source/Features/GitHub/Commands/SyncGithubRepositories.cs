using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Manually re-synchronise the repository list of a single installation that belongs to the
/// current workspace. Idempotent; safe to call repeatedly.
/// </summary>
public record SyncGithubRepositoriesCommand(Guid InstallationId) : ICommand<Result<SyncGithubRepositoriesResponse>>;

public sealed record SyncGithubRepositoriesResponse
{
    public required int Added { get; init; }
    public required int Updated { get; init; }
    public required int Removed { get; init; }
    public required int Total { get; init; }
}

public sealed class SyncGithubRepositoriesHandler
    : ICommandHandler<SyncGithubRepositoriesCommand, Result<SyncGithubRepositoriesResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;
    private readonly IGithubRepositorySyncService _sync;

    public SyncGithubRepositoriesHandler(
        ApplicationDbContext db,
        IWorkspaceContext ctx,
        IGithubRepositorySyncService sync)
    {
        _db = db;
        _ctx = ctx;
        _sync = sync;
    }

    public async Task<Result<SyncGithubRepositoriesResponse>> Handle(
        SyncGithubRepositoriesCommand request,
        CancellationToken cancellationToken)
    {
        if (request.InstallationId == Guid.Empty)
        {
            return Result.Failure<SyncGithubRepositoriesResponse>("installationId is required");
        }

        var installation = await _db.GithubInstallations
            .Where(i => i.Id == request.InstallationId && i.WorkspaceId == _ctx.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (installation is null)
        {
            return Result.Failure<SyncGithubRepositoriesResponse>("Installation not found");
        }

        var result = await _sync.SyncAsync(installation.Id, installation.InstallationId, cancellationToken);

        return Result.Success(new SyncGithubRepositoriesResponse
        {
            Added = result.Added,
            Updated = result.Updated,
            Removed = result.Removed,
            Total = result.Total,
        });
    }
}
