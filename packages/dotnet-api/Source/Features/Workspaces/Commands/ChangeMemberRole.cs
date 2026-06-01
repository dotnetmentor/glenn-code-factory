using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Change a member's role in the current workspace (resolved via <see cref="IWorkspaceContext"/>).
/// Last-Owner protection: refuses to demote the only Owner.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Admin)]</c>.
/// </summary>
public record ChangeMemberRoleCommand(string TargetUserId, WorkspaceRole NewRole) : ICommand<Result<ChangeMemberRoleResponse>>;

public record ChangeMemberRoleResponse
{
    public required string UserId { get; init; }
    public required WorkspaceRole Role { get; init; }
}

public sealed class ChangeMemberRoleHandler : ICommandHandler<ChangeMemberRoleCommand, Result<ChangeMemberRoleResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public ChangeMemberRoleHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<ChangeMemberRoleResponse>> Handle(ChangeMemberRoleCommand request, CancellationToken cancellationToken)
    {
        var membership = await _db.WorkspaceMemberships
            .SingleOrDefaultAsync(m => m.WorkspaceId == _wsCtx.Id && m.UserId == request.TargetUserId, cancellationToken);
        if (membership is null)
            return Result.Failure<ChangeMemberRoleResponse>("Member not found in this workspace");

        if (membership.Role == request.NewRole)
        {
            // No-op: return the current state.
            return Result.Success(new ChangeMemberRoleResponse { UserId = membership.UserId, Role = membership.Role });
        }

        // Last-Owner protection: cannot demote the sole Owner.
        if (membership.Role == WorkspaceRole.Owner && request.NewRole != WorkspaceRole.Owner)
        {
            var ownerCount = await _db.WorkspaceMemberships
                .CountAsync(m => m.WorkspaceId == _wsCtx.Id && m.Role == WorkspaceRole.Owner, cancellationToken);
            if (ownerCount <= 1)
                return Result.Failure<ChangeMemberRoleResponse>("Cannot demote the last Owner of the workspace");
        }

        var oldRole = membership.Role;
        membership.Role = request.NewRole;

        var workspace = await _db.Workspaces.SingleAsync(w => w.Id == _wsCtx.Id, cancellationToken);

        // Keep the denormalised Workspace.OwnerId in sync if we just promoted a new Owner
        // (so the helper "is owner" lookup stays accurate even if the previous owner is later demoted).
        if (request.NewRole == WorkspaceRole.Owner)
        {
            workspace.OwnerId = membership.UserId;
        }

        workspace.RecordMemberRoleChanged(membership.UserId, oldRole, request.NewRole);

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new ChangeMemberRoleResponse { UserId = membership.UserId, Role = membership.Role });
    }
}
