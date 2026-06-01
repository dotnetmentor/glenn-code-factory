using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Flip a subtask's <c>IsCompleted</c> flag. Calls
/// <c>ProjectKanbanCardSubtask.Toggle</c> which raises
/// <see cref="Events.SubtaskToggled"/> with the new state.
///
/// <para><b>Project scope.</b> The subtask is located through its parent
/// card's <c>ProjectId</c> — a subtask doesn't carry the project directly,
/// so we join through the card to confirm scope. Cross-project lookups return
/// <c>"not_found"</c>.</para>
///
/// <para>Returns the new <c>IsCompleted</c> state so the caller can render
/// the change without a re-read.</para>
/// </summary>
public record ToggleSubtaskCommand(
    Guid ProjectId,
    Guid SubtaskId) : ICommand<Result<bool>>;

public class ToggleSubtaskCommandHandler
    : ICommandHandler<ToggleSubtaskCommand, Result<bool>>
{
    private readonly ApplicationDbContext _db;

    public ToggleSubtaskCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<bool>> Handle(
        ToggleSubtaskCommand request,
        CancellationToken cancellationToken)
    {
        // Join through the parent card to enforce project scope without
        // adding a denormalised ProjectId on the subtask row. The card's
        // global filter hides soft-deleted cards, so subtasks of a deleted
        // card aren't reachable here — consistent with "the card is gone".
        var subtask = await _db.ProjectKanbanCardSubtasks
            .Where(s => s.Id == request.SubtaskId)
            .Where(s => _db.ProjectKanbanCards
                .Any(c => c.Id == s.ProjectKanbanCardId && c.ProjectId == request.ProjectId))
            .FirstOrDefaultAsync(cancellationToken);

        if (subtask is null)
        {
            return Result.Failure<bool>("not_found");
        }

        var toggleResult = subtask.Toggle();
        if (toggleResult.IsFailure)
        {
            return Result.Failure<bool>(toggleResult.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(subtask.IsCompleted);
    }
}
