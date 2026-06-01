using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Soft-delete a kanban card. Calls <c>ProjectKanbanCard.MarkDeleted</c> so
/// the entity raises <see cref="Events.CardDeleted"/>; the DbContext stamps
/// <c>DeletedAt</c> / <c>DeletedBy</c> via the <c>ISoftDelete</c> interceptor.
///
/// <para><b>No re-numbering of remaining cards.</b> Gaps in <c>Position</c>
/// are intentional — the kanban list query orders by <c>Position</c>, so a
/// gap shows as a stable ordering with one fewer card. Renumbering would
/// inflate the audit trail (every surviving card looks "modified" in the
/// <c>UpdatedAt</c> column) for zero user-facing benefit.</para>
///
/// <para><b>Project scope + not-found uniformity.</b> Same convention as the
/// rest of the slice: cross-project lookups return <c>"not_found"</c> rather
/// than <c>"forbidden"</c>.</para>
/// </summary>
public record DeleteProjectKanbanCardCommand(
    Guid ProjectId,
    Guid CardId,
    // Nullable for MCP callers (runtime tokens have no Identity user);
    // REST passes the signed-in user's id. Decorative on this command —
    // the ISoftDelete interceptor stamps DeletedBy from the HTTP context
    // (which has no FK), and runtime attribution lives in McpCall.
    string? ActorUserId) : ICommand<Result<Unit>>;

public class DeleteProjectKanbanCardCommandHandler
    : ICommandHandler<DeleteProjectKanbanCardCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;

    public DeleteProjectKanbanCardCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<Unit>> Handle(
        DeleteProjectKanbanCardCommand request,
        CancellationToken cancellationToken)
    {
        var card = await _db.ProjectKanbanCards
            .FirstOrDefaultAsync(
                c => c.Id == request.CardId && c.ProjectId == request.ProjectId,
                cancellationToken);

        if (card is null)
        {
            return Result.Failure<Unit>("not_found");
        }

        var markResult = card.MarkDeleted();
        if (markResult.IsFailure)
        {
            return Result.Failure<Unit>(markResult.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(Unit.Value);
    }
}
