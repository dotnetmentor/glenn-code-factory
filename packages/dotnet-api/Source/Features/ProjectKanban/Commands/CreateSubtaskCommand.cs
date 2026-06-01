using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Append a new checklist item to a card. Locates the parent card by
/// <see cref="CardId"/> + <see cref="ProjectId"/> (defence-in-depth scope),
/// computes the next 0-based <c>Position</c> within that card's subtask list,
/// calls <see cref="ProjectKanbanCardSubtask.Create"/> (which raises
/// <see cref="Events.SubtaskCreated"/>), and returns the new subtask id.
///
/// <para><b>Project scope.</b> The parent-card lookup filters on both
/// <see cref="CardId"/> and <see cref="ProjectId"/>; cross-project attempts
/// return <c>"not_found"</c> — uniform 404 stance like the rest of the slice.</para>
/// </summary>
public record CreateSubtaskCommand(
    Guid ProjectId,
    Guid CardId,
    string Title) : ICommand<Result<Guid>>;

public class CreateSubtaskCommandHandler
    : ICommandHandler<CreateSubtaskCommand, Result<Guid>>
{
    private readonly ApplicationDbContext _db;

    public CreateSubtaskCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<Guid>> Handle(
        CreateSubtaskCommand request,
        CancellationToken cancellationToken)
    {
        // Up-front title check so the entity guard's ArgumentException is the
        // programmer-error path, not the wire-validation path.
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Result.Failure<Guid>("invalid_title");
        }

        if (request.Title.Length > ProjectKanbanCardSubtask.MaxTitleLength)
        {
            return Result.Failure<Guid>("invalid_title");
        }

        // Locate parent card scoped to the runtime's project. We don't need to
        // load it tracked (we only need to confirm it exists in this project);
        // AsNoTracking keeps the read cheap.
        var cardExists = await _db.ProjectKanbanCards
            .AsNoTracking()
            .AnyAsync(
                c => c.Id == request.CardId && c.ProjectId == request.ProjectId,
                cancellationToken);

        if (!cardExists)
        {
            return Result.Failure<Guid>("not_found");
        }

        // Next position = (max in card's subtask list) + 1, or 0 for an empty
        // checklist. Same coalesce-to-(-1)-then-+1 trick the kanban card
        // create uses.
        var maxPosition = await _db.ProjectKanbanCardSubtasks
            .Where(s => s.ProjectKanbanCardId == request.CardId)
            .Select(s => (int?)s.Position)
            .MaxAsync(cancellationToken);

        var nextPosition = (maxPosition ?? -1) + 1;

        ProjectKanbanCardSubtask subtask;
        try
        {
            subtask = ProjectKanbanCardSubtask.Create(request.CardId, request.Title, nextPosition);
        }
        catch (ArgumentException)
        {
            return Result.Failure<Guid>("invalid_title");
        }

        _db.ProjectKanbanCardSubtasks.Add(subtask);
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(subtask.Id);
    }
}
