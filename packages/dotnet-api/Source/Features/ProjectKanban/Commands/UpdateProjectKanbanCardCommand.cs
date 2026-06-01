using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Partial-update a kanban card's editable fields (<c>Title</c> /
/// <c>Description</c> / <c>Priority</c> / <c>DueDate</c>). <c>null</c> on a
/// field means "leave unchanged" — the daemon's MCP client passes only the
/// fields its caller wanted to mutate.
///
/// <para><b>Project scope.</b> The handler verifies the loaded row's
/// <c>ProjectId</c> matches <see cref="ProjectId"/>; on mismatch it returns
/// <c>"not_found"</c>, never <c>"forbidden"</c>, to avoid leaking
/// cross-tenant existence (same convention as
/// <see cref="GetProjectKanbanCardQuery"/>).</para>
///
/// <para><b>Rich-entity refactor (Card 2).</b> The handler reads the current
/// values into a working set, merges the supplied non-null fields, then calls
/// <see cref="ProjectKanbanCard.UpdateMetadata"/> to commit the change and
/// raise <see cref="Events.CardUpdated"/>. <see cref="ActorUserId"/> is
/// nullable and accepted for symmetry with the rest of the slice — the
/// handler doesn't write it to a column, so the MCP path (no Identity user)
/// passes <c>null</c> and the audit trail lives in the <c>McpCall</c> row
/// written by the framework.</para>
/// </summary>
public record UpdateProjectKanbanCardCommand(
    Guid ProjectId,
    Guid CardId,
    string? Title,
    string? Description,
    // Nullable for MCP callers; REST passes the signed-in user's id.
    // Decorative on this command — handler doesn't persist it.
    string? ActorUserId,
    ProjectKanbanCardPriority? Priority = null,
    DateTime? DueDate = null,
    bool ClearDueDate = false) : ICommand<Result<ProjectKanbanCardDto>>;

public class UpdateProjectKanbanCardCommandHandler
    : ICommandHandler<UpdateProjectKanbanCardCommand, Result<ProjectKanbanCardDto>>
{
    private readonly ApplicationDbContext _db;

    public UpdateProjectKanbanCardCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectKanbanCardDto>> Handle(
        UpdateProjectKanbanCardCommand request,
        CancellationToken cancellationToken)
    {
        // Defence in depth: filter both Id AND ProjectId so a leaked card id
        // from another tenant never resolves through this handler.
        var card = await _db.ProjectKanbanCards
            .FirstOrDefaultAsync(
                c => c.Id == request.CardId && c.ProjectId == request.ProjectId,
                cancellationToken);

        if (card is null)
        {
            return Result.Failure<ProjectKanbanCardDto>("not_found");
        }

        // Partial update — null = leave unchanged. We compute the would-be
        // state and hand it to the entity's UpdateMetadata method, which
        // owns validation + event raising.
        var newTitle = request.Title ?? card.Title;

        if (request.Title is not null)
        {
            var titleError = KanbanCardValidation.ValidateTitle(request.Title);
            if (titleError is not null)
            {
                return Result.Failure<ProjectKanbanCardDto>(titleError);
            }
        }

        // Description: null on the request means "leave unchanged"; "" is an
        // explicit clear (stored verbatim).
        var newDescription = request.Description is not null ? request.Description : card.Description;
        var newPriority = request.Priority ?? card.Priority;

        // DueDate semantics: a non-null DueDate sets the date. ClearDueDate
        // is the explicit "set to null" signal so we can distinguish "unchanged"
        // (null + ClearDueDate=false) from "clear it" (null + ClearDueDate=true).
        DateTime? newDueDate;
        if (request.ClearDueDate)
        {
            newDueDate = null;
        }
        else if (request.DueDate.HasValue)
        {
            newDueDate = request.DueDate;
        }
        else
        {
            newDueDate = card.DueDate;
        }

        var update = card.UpdateMetadata(newTitle, newDescription, newPriority, newDueDate);
        if (update.IsFailure)
        {
            return Result.Failure<ProjectKanbanCardDto>(update.Error!);
        }

        // SaveChangesAsync stamps UpdatedAt via the IAuditable interceptor.
        await _db.SaveChangesAsync(cancellationToken);

        // Re-load subtasks for the response DTO — the update path may surface
        // them in the same response shape the read query uses.
        var subtasks = await _db.ProjectKanbanCardSubtasks
            .AsNoTracking()
            .Where(s => s.ProjectKanbanCardId == card.Id)
            .OrderBy(s => s.Position)
            .Select(s => new ProjectKanbanCardSubtaskDto(
                s.Id, s.ProjectKanbanCardId, s.Title, s.IsCompleted, s.Position))
            .ToListAsync(cancellationToken);

        return Result.Success(new ProjectKanbanCardDto(
            card.Id,
            card.ProjectId,
            card.Title,
            card.Description,
            card.Status,
            card.Position,
            card.Priority,
            card.DueDate,
            card.Source,
            card.CreatedOnBranch,
            card.CreatedAt,
            card.UpdatedAt,
            subtasks));
    }
}
