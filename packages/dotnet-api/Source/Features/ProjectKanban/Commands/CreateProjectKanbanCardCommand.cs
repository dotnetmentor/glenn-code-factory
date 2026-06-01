using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Create a new kanban card. Position is computed server-side: the new card
/// lands at the end of its <see cref="Status"/> bucket (i.e. one past the
/// current max <c>Position</c> in <c>(ProjectId, Status)</c>, or 0 for an
/// empty bucket). Mirrors the "always append" UX of every kanban tool — a
/// future "insert at position N" surface can layer on the same primitive.
///
/// <para><b>Project scope.</b> The command takes <see cref="ProjectId"/>
/// explicitly so the seam between the controller (claims-derived scope) and
/// the handler (filter on every query) is unambiguous.
/// <see cref="ActorUserId"/> is the value stamped into the entity's
/// <c>CreatedBy</c> column — REST passes the signed-in user's id; the MCP
/// path passes <c>null</c> because the runtime token has no Identity user
/// behind it and the column FKs into <c>AspNetUsers</c>. Mirrors
/// <c>SaveSpecificationCommand.CreatedBy</c>'s convention; the per-call
/// audit row in <c>McpCall</c> carries the runtime attribution.</para>
///
/// <para><b>Card 2 fields.</b> <see cref="Priority"/> defaults to
/// <see cref="ProjectKanbanCardPriority.Medium"/> and <see cref="DueDate"/> is
/// nullable; existing callers that don't set them get the legacy behaviour for
/// free.</para>
/// </summary>
public record CreateProjectKanbanCardCommand(
    Guid ProjectId,
    string Title,
    string? Description,
    ProjectKanbanCardStatus Status,
    // Nullable: the entity's CreatedBy column FKs into AspNetUsers, so MCP
    // callers (runtime tokens with no Identity user) must pass null.
    // REST passes the signed-in user's id.
    string? ActorUserId,
    ProjectKanbanCardPriority Priority = ProjectKanbanCardPriority.Medium,
    DateTime? DueDate = null,
    ProjectKanbanCardSource Source = ProjectKanbanCardSource.Human,
    string? CreatedOnBranch = null) : ICommand<Result<ProjectKanbanCardDto>>;

public class CreateProjectKanbanCardCommandHandler
    : ICommandHandler<CreateProjectKanbanCardCommand, Result<ProjectKanbanCardDto>>
{
    private readonly ApplicationDbContext _db;

    public CreateProjectKanbanCardCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectKanbanCardDto>> Handle(
        CreateProjectKanbanCardCommand request,
        CancellationToken cancellationToken)
    {
        // Up-front cheap validation so the entity factory's ArgumentException
        // path only runs on truly impossible input (defence-in-depth). Mirrors
        // SaveSpecificationCommand: handler returns Result.Failure shapes, the
        // entity throws only on programmer-error.
        var titleError = KanbanCardValidation.ValidateTitle(request.Title);
        if (titleError is not null)
        {
            return Result.Failure<ProjectKanbanCardDto>(titleError);
        }

        // Position = (max in bucket) + 1, or 0 if the bucket is empty. Empty
        // bucket maxBy returns null on EF/InMemory — coalesce to -1 then +1
        // so a fresh column lands at 0 and the contract is "0-based".
        var maxPosition = await _db.ProjectKanbanCards
            .Where(c => c.ProjectId == request.ProjectId && c.Status == request.Status)
            .Select(c => (int?)c.Position)
            .MaxAsync(cancellationToken);

        var nextPosition = (maxPosition ?? -1) + 1;

        ProjectKanbanCard card;
        try
        {
            card = ProjectKanbanCard.Create(
                request.ProjectId,
                request.Title,
                request.Description,
                request.Status,
                nextPosition,
                request.Priority,
                request.DueDate,
                request.ActorUserId,
                request.Source,
                request.CreatedOnBranch);
        }
        catch (ArgumentException)
        {
            // Entity-level guard re-validates title; the up-front check above
            // covers the common cases but the factory is the source of truth.
            return Result.Failure<ProjectKanbanCardDto>("invalid_title");
        }

        _db.ProjectKanbanCards.Add(card);
        await _db.SaveChangesAsync(cancellationToken);

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
            Subtasks: new List<ProjectKanbanCardSubtaskDto>()));
    }
}
