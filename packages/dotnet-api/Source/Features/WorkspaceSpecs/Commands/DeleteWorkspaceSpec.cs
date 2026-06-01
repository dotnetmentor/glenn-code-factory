using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Commands;

/// <summary>
/// Hard-delete a catalog spec. No soft-delete pattern is needed: branches
/// previously forked from this spec are unaffected because the spec was
/// copied into their runtime <c>Spec</c> at fork time (snapshot semantic).
/// </summary>
public record DeleteWorkspaceSpecCommand(
    Guid WorkspaceId,
    Guid SpecId,
    string CallerUserId
) : ICommand<Result>;

public sealed class DeleteWorkspaceSpecHandler : ICommandHandler<DeleteWorkspaceSpecCommand, Result>
{
    private readonly ApplicationDbContext _db;

    public DeleteWorkspaceSpecHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result> Handle(
        DeleteWorkspaceSpecCommand request,
        CancellationToken cancellationToken)
    {
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure("not_a_member");
        }

        var spec = await _db.WorkspaceSpecs
            .SingleOrDefaultAsync(
                s => s.Id == request.SpecId && s.WorkspaceId == request.WorkspaceId,
                cancellationToken);
        if (spec is null)
        {
            return Result.Failure("spec_not_found");
        }

        _db.WorkspaceSpecs.Remove(spec);
        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
