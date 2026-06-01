using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Move a kanban card to a new <see cref="NewStatus"/> column and
/// <see cref="NewPosition"/> within that column. The reorder is atomic — every
/// affected row in the source and destination buckets is shifted in the same
/// <c>SaveChangesAsync</c> call so a mid-flight failure can never leave a
/// gapped or duplicated <c>Position</c>.
///
/// <para><b>Transaction.</b> Relies on the implicit single-<c>SaveChangesAsync</c>
/// transaction provided by EF Core (Read Committed). An explicit
/// <c>BeginTransactionAsync(Serializable)</c> wrap was added 2026-05-25 and
/// reverted 2026-05-27 — it threw 100% of the time because the DbContext is
/// configured with <c>NpgsqlRetryingExecutionStrategy</c>, which forbids
/// user-initiated transactions outside an <c>ExecuteAsync</c> delegate. The
/// race it tried to defend against (two concurrent reorders on overlapping
/// position ranges in the same project) has never been observed in production
/// traffic — agents serialize their card operations, and the human UI is
/// single-actor. If concurrent reorders ever produce duplicate <c>Position</c>
/// values in the wild, revisit with either a single SQL UPDATE for the shift
/// or the proper <c>strategy.ExecuteAsync(...)</c> wrapper (which would also
/// need to handle entity-tracking on retry — see commit 9913422 for what NOT
/// to do).</para>
///
/// <para><b>Algorithm.</b> Three cases by bucket equality:
/// <list type="number">
///   <item><b>Same bucket, same position:</b> no-op (still returns the card).</item>
///   <item><b>Same bucket, different position:</b> shift the contiguous range
///         between <c>oldPosition</c> and <c>NewPosition</c> by ±1 — direction
///         depends on whether the move is "up" or "down" the list. The moved
///         card itself takes <c>NewPosition</c>.</item>
///   <item><b>Different bucket:</b> close the gap in the source bucket
///         (shift positions &gt; <c>oldPosition</c> down by 1), open a slot in
///         the destination bucket (shift positions &gt;= <c>NewPosition</c> up
///         by 1), then write the moved card's new <c>Status</c> /
///         <c>Position</c>.</item>
/// </list>
/// The handler updates sibling positions directly (siblings remain
/// CRUD-anemic for this internal reorder), and only the moved card itself
/// goes through <see cref="ProjectKanbanCard.Move"/> so the
/// <see cref="Events.CardMoved"/> event fires exactly once with the
/// before / after status + position payload.</para>
///
/// <para><b>NewPosition is clamped to the valid range</b> [0, count-in-bucket]
/// so a caller passing 9999 lands at the end rather than creating a gap. Same
/// "tolerant of bad input" stance the rest of the framework takes — the daemon
/// is a friendly client, we'd rather absorb the mistake than reject and force
/// a retry.</para>
///
/// <para><b>Project scope.</b> The card is filtered on both <see cref="CardId"/>
/// and <see cref="ProjectId"/>; cross-project lookups return <c>"not_found"</c>.
/// All bucket queries also filter by <see cref="ProjectId"/> so a stale id
/// can't reach across projects via the shift step either.</para>
/// </summary>
public record MoveProjectKanbanCardCommand(
    Guid ProjectId,
    Guid CardId,
    ProjectKanbanCardStatus NewStatus,
    int NewPosition,
    // Nullable for MCP callers (runtime tokens have no Identity user);
    // REST passes the signed-in user's id. Decorative on this command —
    // handler doesn't persist it; runtime attribution is in McpCall.
    string? ActorUserId) : ICommand<Result<ProjectKanbanCardDto>>;

public class MoveProjectKanbanCardCommandHandler
    : ICommandHandler<MoveProjectKanbanCardCommand, Result<ProjectKanbanCardDto>>
{
    private readonly ApplicationDbContext _db;

    public MoveProjectKanbanCardCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectKanbanCardDto>> Handle(
        MoveProjectKanbanCardCommand request,
        CancellationToken cancellationToken)
    {
        if (request.NewPosition < 0)
        {
            return Result.Failure<ProjectKanbanCardDto>("invalid_position");
        }

        var card = await _db.ProjectKanbanCards
            .FirstOrDefaultAsync(
                c => c.Id == request.CardId && c.ProjectId == request.ProjectId,
                cancellationToken);

        if (card is null)
        {
            return Result.Failure<ProjectKanbanCardDto>("not_found");
        }

        var oldStatus = card.Status;
        var oldPosition = card.Position;
        var newStatus = request.NewStatus;

        // Pull every other card in the source and destination buckets — we
        // need them tracked so the SaveChanges below shifts their positions
        // as part of the same write. Two buckets at most, both filtered by
        // ProjectId so no cross-tenant rows could be touched.
        var affected = await _db.ProjectKanbanCards
            .Where(c =>
                c.ProjectId == request.ProjectId
                && (c.Status == oldStatus || c.Status == newStatus)
                && c.Id != request.CardId)
            .ToListAsync(cancellationToken);

        int finalPosition;
        if (oldStatus == newStatus)
        {
            // Single-bucket move. Clamp to [0, bucketSize-1] so a caller
            // passing a too-large value lands at the end instead of creating
            // a gap. bucketSize counts the moved card itself.
            var bucketSize = affected.Count(c => c.Status == oldStatus) + 1;
            var clamped = Math.Min(request.NewPosition, bucketSize - 1);
            finalPosition = clamped;

            if (clamped != oldPosition)
            {
                if (clamped > oldPosition)
                {
                    // Moving down: cards strictly between (oldPos, clamped] shift up by 1.
                    foreach (var other in affected.Where(c => c.Status == oldStatus
                                                              && c.Position > oldPosition
                                                              && c.Position <= clamped))
                    {
                        other.ShiftPosition(-1);
                    }
                }
                else // clamped < oldPosition
                {
                    // Moving up: cards in [clamped, oldPos) shift down by 1.
                    foreach (var other in affected.Where(c => c.Status == oldStatus
                                                              && c.Position >= clamped
                                                              && c.Position < oldPosition))
                    {
                        other.ShiftPosition(1);
                    }
                }
            }
        }
        else
        {
            // Cross-bucket move.
            //
            // 1) Close the gap in the source bucket: every card with position
            //    > oldPosition shifts down by 1.
            foreach (var other in affected.Where(c => c.Status == oldStatus && c.Position > oldPosition))
            {
                other.ShiftPosition(-1);
            }

            // 2) Clamp NewPosition to [0, destSize] — destSize is the count
            //    in the destination bucket BEFORE inserting the moved card,
            //    so the valid landing range is [0, destSize].
            var destSize = affected.Count(c => c.Status == newStatus);
            var clamped = Math.Min(request.NewPosition, destSize);
            finalPosition = clamped;

            // 3) Open a slot in the destination bucket: shift everything >=
            //    clamped up by 1.
            foreach (var other in affected.Where(c => c.Status == newStatus && c.Position >= clamped))
            {
                other.ShiftPosition(1);
            }
        }

        // 4) Commit the move on the card itself via the rich-entity method so
        //    CardMoved is raised with the before/after status + position.
        //    Even when the call is a logical no-op (same bucket, same position)
        //    we still surface the event — observers can dedupe trivially and
        //    the audit trail benefits from the breadcrumb.
        var moveResult = card.Move(newStatus, finalPosition);
        if (moveResult.IsFailure)
        {
            return Result.Failure<ProjectKanbanCardDto>(moveResult.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(await ToDtoAsync(card, cancellationToken));
    }

    private async Task<ProjectKanbanCardDto> ToDtoAsync(
        ProjectKanbanCard card,
        CancellationToken cancellationToken)
    {
        var subtasks = await _db.ProjectKanbanCardSubtasks
            .AsNoTracking()
            .Where(s => s.ProjectKanbanCardId == card.Id)
            .OrderBy(s => s.Position)
            .Select(s => new ProjectKanbanCardSubtaskDto(
                s.Id, s.ProjectKanbanCardId, s.Title, s.IsCompleted, s.Position))
            .ToListAsync(cancellationToken);

        return new ProjectKanbanCardDto(
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
            subtasks);
    }
}
