using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Accept a workspace invite by token. The caller must already be authenticated; their email
/// must match the invite's email (case-insensitive). On success a <see cref="WorkspaceMembership"/>
/// is created, the invite is marked accepted, and the event store records the transition.
///
/// This handler is NOT workspace-scoped (it runs before the user is a member). Top-level endpoint.
/// </summary>
public record AcceptInviteCommand(string UserId, string Token) : ICommand<Result<AcceptInviteResponse>>;

public record AcceptInviteResponse
{
    public required Guid WorkspaceId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required WorkspaceRole Role { get; init; }
}

public sealed class AcceptInviteHandler : ICommandHandler<AcceptInviteCommand, Result<AcceptInviteResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<User> _userManager;

    public AcceptInviteHandler(ApplicationDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<Result<AcceptInviteResponse>> Handle(AcceptInviteCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Result.Failure<AcceptInviteResponse>("Token is required");

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user is null)
            return Result.Failure<AcceptInviteResponse>("User not found");

        var invite = await _db.WorkspaceInvites
            .Include(i => i.Workspace)
            .SingleOrDefaultAsync(i => i.Token == request.Token, cancellationToken);
        if (invite is null)
            return Result.Failure<AcceptInviteResponse>("Invite not found");

        if (invite.AcceptedAt is not null)
            return Result.Failure<AcceptInviteResponse>("Invite has already been accepted");

        if (invite.ExpiresAt <= DateTime.UtcNow)
            return Result.Failure<AcceptInviteResponse>("Invite has expired");

        // Email-match check: invite is bound to a specific email; the accepting user must own it.
        if (!string.Equals(invite.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<AcceptInviteResponse>("This invite was issued to a different email address");

        // If the user is already a member (e.g. a duplicate invite slipped through), short-circuit gracefully.
        var existing = await _db.WorkspaceMemberships
            .SingleOrDefaultAsync(m => m.WorkspaceId == invite.WorkspaceId && m.UserId == user.Id, cancellationToken);

        if (existing is null)
        {
            _db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = invite.WorkspaceId,
                UserId = user.Id,
                Role = invite.Role,
            });
            invite.Workspace.RecordMemberAdded(user.Id, invite.Role);
        }

        invite.AcceptedAt = DateTime.UtcNow;
        invite.AcceptedByUserId = user.Id;
        invite.Workspace.RaiseInviteAccepted(invite.Id, user.Id);

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new AcceptInviteResponse
        {
            WorkspaceId = invite.Workspace.Id,
            Slug = invite.Workspace.Slug,
            Name = invite.Workspace.Name,
            Role = invite.Role,
        });
    }
}
