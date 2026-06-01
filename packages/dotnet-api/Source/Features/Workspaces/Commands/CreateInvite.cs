using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Events;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Create a workspace invite for the given email at the given role. Generates a globally-unique
/// URL-safe token; the token is the secret the recipient pastes into the accept-link.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Admin)]</c>.
/// </summary>
public record CreateInviteCommand(string Email, WorkspaceRole Role) : ICommand<Result<CreateInviteResponse>>;

public record CreateInviteResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required WorkspaceRole Role { get; init; }
    public required string Token { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

public sealed class CreateInviteHandler : ICommandHandler<CreateInviteCommand, Result<CreateInviteResponse>>
{
    private const int InviteTtlDays = 7;

    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public CreateInviteHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<CreateInviteResponse>> Handle(CreateInviteCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return Result.Failure<CreateInviteResponse>("Email is required");

        var email = request.Email.Trim().ToLowerInvariant();

        // If the email already belongs to a member of this workspace, no invite needed.
        var alreadyMember = await _db.WorkspaceMemberships
            .AnyAsync(m => m.WorkspaceId == _wsCtx.Id && m.User.Email!.ToLower() == email, cancellationToken);
        if (alreadyMember)
            return Result.Failure<CreateInviteResponse>("That user is already a member of this workspace");

        // If a pending invite already exists for the same email, refuse to create a duplicate —
        // the caller can revoke and re-create if they want different terms.
        var now = DateTime.UtcNow;
        var existingPending = await _db.WorkspaceInvites
            .AnyAsync(i => i.WorkspaceId == _wsCtx.Id
                        && i.Email == email
                        && i.AcceptedAt == null
                        && i.ExpiresAt > now, cancellationToken);
        if (existingPending)
            return Result.Failure<CreateInviteResponse>("A pending invite already exists for that email");

        var token = GenerateToken();
        var invite = new WorkspaceInvite
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _wsCtx.Id,
            Email = email,
            Role = request.Role,
            InvitedById = _wsCtx.UserId,
            Token = token,
            ExpiresAt = now.AddDays(InviteTtlDays),
        };

        _db.WorkspaceInvites.Add(invite);

        var workspace = await _db.Workspaces.SingleAsync(w => w.Id == _wsCtx.Id, cancellationToken);
        // Reuse the workspace's domain-event channel so the event is captured by the interceptor.
        workspace.RaiseInviteCreated(invite.Id, email, request.Role);

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateInviteResponse
        {
            Id = invite.Id,
            Email = invite.Email,
            Role = invite.Role,
            Token = invite.Token,
            ExpiresAt = invite.ExpiresAt,
        });
    }

    /// <summary>
    /// Generate a 32-byte cryptographically random token, base64url-encoded so it's URL-safe.
    /// </summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
