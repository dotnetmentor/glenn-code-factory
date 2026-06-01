using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Remove a member from the current workspace (resolved via <see cref="IWorkspaceContext"/>).
/// Last-Owner protection: refuses to remove the only Owner.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Admin)]</c>.
/// </summary>
public record RemoveMemberCommand(string TargetUserId) : ICommand<Result>;

public sealed class RemoveMemberHandler : ICommandHandler<RemoveMemberCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public RemoveMemberHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result> Handle(RemoveMemberCommand request, CancellationToken cancellationToken)
    {
        var membership = await _db.WorkspaceMemberships
            .SingleOrDefaultAsync(m => m.WorkspaceId == _wsCtx.Id && m.UserId == request.TargetUserId, cancellationToken);
        if (membership is null)
            return Result.Failure("Member not found in this workspace");

        // Last-Owner protection: cannot remove the sole Owner.
        if (membership.Role == WorkspaceRole.Owner)
        {
            var ownerCount = await _db.WorkspaceMemberships
                .CountAsync(m => m.WorkspaceId == _wsCtx.Id && m.Role == WorkspaceRole.Owner, cancellationToken);
            if (ownerCount <= 1)
                return Result.Failure("Cannot remove the last Owner of the workspace");
        }

        _db.WorkspaceMemberships.Remove(membership);

        var workspace = await _db.Workspaces.SingleAsync(w => w.Id == _wsCtx.Id, cancellationToken);
        workspace.RecordMemberRemoved(membership.UserId);

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
