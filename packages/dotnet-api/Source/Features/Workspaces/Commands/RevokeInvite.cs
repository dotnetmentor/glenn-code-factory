using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Revoke a pending invite by id (scoped to the current workspace via <see cref="IWorkspaceContext"/>).
/// Hard-delete: revoked invites have no audit value once retracted; the WorkspaceInviteAccepted
/// event still tells you who joined and when.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Admin)]</c>.
/// </summary>
public record RevokeInviteCommand(Guid InviteId) : ICommand<Result>;

public sealed class RevokeInviteHandler : ICommandHandler<RevokeInviteCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public RevokeInviteHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result> Handle(RevokeInviteCommand request, CancellationToken cancellationToken)
    {
        var invite = await _db.WorkspaceInvites
            .SingleOrDefaultAsync(i => i.Id == request.InviteId && i.WorkspaceId == _wsCtx.Id, cancellationToken);
        if (invite is null)
            return Result.Failure("Invite not found");

        if (invite.AcceptedAt is not null)
            return Result.Failure("Cannot revoke an invite that has already been accepted");

        _db.WorkspaceInvites.Remove(invite);
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
