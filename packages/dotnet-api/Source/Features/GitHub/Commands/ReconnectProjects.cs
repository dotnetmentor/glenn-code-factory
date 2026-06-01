using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Re-link detached projects (<c>Project.GithubInstallationId == NULL</c>) inside
/// the current workspace to a freshly-installed <see cref="Models.GithubInstallation"/>.
///
/// <para>The match key is <c>Project.GithubRepoOwner == GithubInstallation.AccountLogin</c>
/// (case-insensitive). The disconnect flow soft-detaches by setting the FK to NULL,
/// preserving the human-readable repo coordinates the project was originally created
/// with — so when the same owner / org reinstalls the GitHub App we can rejoin all
/// previously-detached projects in one bulk update.</para>
///
/// <para>Idempotent: running with no detached matches simply returns
/// <c>ReconnectedCount = 0</c>.</para>
/// </summary>
public record ReconnectProjectsCommand(Guid InstallationId)
    : ICommand<Result<ReconnectProjectsResponse>>;

/// <summary>
/// Outcome of a <see cref="ReconnectProjectsCommand"/>. Carries the count + the
/// list of re-linked project ids so the frontend can refetch / refresh exactly
/// the rows that changed (the projects list page re-invalidates on this id set).
/// </summary>
public sealed record ReconnectProjectsResponse(
    int ReconnectedCount,
    IReadOnlyList<Guid> ProjectIds);

public sealed class ReconnectProjectsHandler
    : ICommandHandler<ReconnectProjectsCommand, Result<ReconnectProjectsResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;

    public ReconnectProjectsHandler(ApplicationDbContext db, IWorkspaceContext ctx)
    {
        _db = db;
        _ctx = ctx;
    }

    public async Task<Result<ReconnectProjectsResponse>> Handle(
        ReconnectProjectsCommand request,
        CancellationToken cancellationToken)
    {
        // Tenancy: the installation must belong to the current workspace.
        // Mirrors RemoveGithubInstallationHandler — a member of workspace A
        // must not be able to drive installations attached to workspace B.
        var installation = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == request.InstallationId && i.WorkspaceId == _ctx.Id)
            .Select(i => new { i.Id, i.AccountLogin })
            .SingleOrDefaultAsync(cancellationToken);

        if (installation is null)
        {
            return Result.Failure<ReconnectProjectsResponse>("Installation not found");
        }

        var workspaceId = _ctx.Id;

        // Match key: GithubRepoOwner == AccountLogin, case-insensitive. EF's
        // ILike compiles to Postgres' native ILIKE — no client-side ToLower
        // and the existing GithubRepoOwner index still applies for the workspace
        // filter (the workspace + IsDeleted clauses are the selective ones).
        var candidates = await _db.Projects
            .Where(p => p.WorkspaceId == workspaceId
                        && p.GithubInstallationId == null
                        && EF.Functions.ILike(p.GithubRepoOwner, installation.AccountLogin))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return Result.Success(new ReconnectProjectsResponse(
                ReconnectedCount: 0,
                ProjectIds: Array.Empty<Guid>()));
        }

        var ids = new List<Guid>(candidates.Count);
        foreach (var project in candidates)
        {
            project.GithubInstallationId = installation.Id;
            ids.Add(project.Id);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new ReconnectProjectsResponse(
            ReconnectedCount: ids.Count,
            ProjectIds: ids));
    }
}
