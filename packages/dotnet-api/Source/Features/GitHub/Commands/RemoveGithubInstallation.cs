using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Disconnect a GitHub installation from the current workspace. Removes the installation row
/// and all repo rows under it; any projects pointing at the installation are detached
/// automatically by the DB's <c>ON DELETE SET NULL</c> cascade — they survive the disconnect
/// in a "detached" state and can be re-linked later via <c>ReconnectProjects</c>.
///
/// <para>Does NOT call back to GitHub — the user must revoke the install on github.com
/// themselves; the controller surfaces this in its docs.</para>
/// </summary>
public record RemoveGithubInstallationCommand(Guid InstallationId) : ICommand<Result>;

public sealed class RemoveGithubInstallationHandler
    : ICommandHandler<RemoveGithubInstallationCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;

    public RemoveGithubInstallationHandler(ApplicationDbContext db, IWorkspaceContext ctx)
    {
        _db = db;
        _ctx = ctx;
    }

    public async Task<Result> Handle(RemoveGithubInstallationCommand request, CancellationToken cancellationToken)
    {
        var installation = await _db.GithubInstallations
            .Where(i => i.Id == request.InstallationId && i.WorkspaceId == _ctx.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (installation is null)
        {
            return Result.Failure("Installation not found");
        }

        // GithubInstallation does NOT implement ISoftDelete — hard-delete with cascade.
        // (See P2.3 caveats: adding ISoftDelete is a separate refactor.)
        // Repos under this installation are owned 1:1 and don't survive the disconnect.
        var repos = await _db.GithubRepositories
            .Where(r => r.GithubInstallationId == installation.Id)
            .ToListAsync(cancellationToken);
        if (repos.Count > 0)
        {
            _db.GithubRepositories.RemoveRange(repos);
        }
        _db.GithubInstallations.Remove(installation);

        // Projects pointing at this installation are detached automatically by the
        // SetNull FK cascade configured in ApplicationDbContext — no extra work here.
        // The detached projects survive and can be reconnected via ReconnectProjects.
        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
