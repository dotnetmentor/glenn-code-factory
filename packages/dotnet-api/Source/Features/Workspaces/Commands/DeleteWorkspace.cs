using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Soft-delete the workspace in <see cref="IWorkspaceContext"/>. Owner-only.
/// (The filter is what enforces Owner; this handler just performs the deletion.)
/// </summary>
public record DeleteWorkspaceCommand : ICommand<Result>;

public sealed class DeleteWorkspaceHandler : ICommandHandler<DeleteWorkspaceCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public DeleteWorkspaceHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result> Handle(DeleteWorkspaceCommand request, CancellationToken cancellationToken)
    {
        var workspace = await _db.Workspaces.SingleOrDefaultAsync(w => w.Id == _wsCtx.Id, cancellationToken);
        if (workspace is null) return Result.Failure("Workspace not found");

        workspace.IsDeleted = true; // DbContext will populate DeletedAt + DeletedBy
        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
