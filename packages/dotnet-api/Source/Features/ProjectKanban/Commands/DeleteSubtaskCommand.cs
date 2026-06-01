using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Soft-delete a subtask. Calls <c>ProjectKanbanCardSubtask.MarkDeleted</c>
/// which raises <see cref="Events.SubtaskDeleted"/>; the DbContext stamps
/// <c>DeletedAt</c> / <c>DeletedBy</c> via the <c>ISoftDelete</c> interceptor.
///
/// <para><b>Project scope.</b> Located through the parent card's
/// <c>ProjectId</c> (subtasks don't carry the project directly). Cross-project
/// lookups return <c>"not_found"</c> — uniform 404 stance, no cross-tenant
/// existence leak.</para>
/// </summary>
public record DeleteSubtaskCommand(
    Guid ProjectId,
    Guid SubtaskId) : ICommand<Result<Unit>>;

public class DeleteSubtaskCommandHandler
    : ICommandHandler<DeleteSubtaskCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;

    public DeleteSubtaskCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<Unit>> Handle(
        DeleteSubtaskCommand request,
        CancellationToken cancellationToken)
    {
        var subtask = await _db.ProjectKanbanCardSubtasks
            .Where(s => s.Id == request.SubtaskId)
            .Where(s => _db.ProjectKanbanCards
                .Any(c => c.Id == s.ProjectKanbanCardId && c.ProjectId == request.ProjectId))
            .FirstOrDefaultAsync(cancellationToken);

        if (subtask is null)
        {
            return Result.Failure<Unit>("not_found");
        }

        var deleteResult = subtask.MarkDeleted();
        if (deleteResult.IsFailure)
        {
            return Result.Failure<Unit>(deleteResult.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(Unit.Value);
    }
}
