using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Conversations.Commands;

/// <summary>
/// Reorder the <em>currently queued</em> sessions on a runtime. The handler
/// loads every <see cref="AgentSessionStatus.Pending"/> session with a non-null
/// <see cref="AgentSession.QueuePosition"/> for the runtime, compares it as a
/// set against <see cref="SessionIds"/>, and — only on an exact match —
/// renumbers <see cref="AgentSession.QueuePosition"/> 1-based in the supplied
/// order.
///
/// <para><b>Optimistic-concurrency simulation.</b> If the request set doesn't
/// exactly match the DB set (extra ids, missing ids, duplicates), the handler
/// returns <c>Result.Failure</c> with <c>"queue mismatch — refresh"</c> and
/// makes <em>no</em> mutations. Two clients dragging concurrently will see
/// the second one bounce back to refresh — simpler than a row version stamp
/// and matches the dispatch-next handler's behaviour of treating the queue
/// as snapshot-at-read.</para>
///
/// <para><b>Why a single SaveChanges and no transaction.</b> All
/// <c>QueuePosition</c> updates land in one EF batch — Postgres makes that
/// atomic without an explicit transaction. The <see cref="QueueReordered"/>
/// event is published <em>after</em> SaveChanges so the audit row never lies
/// about a reshuffle that didn't commit.</para>
///
/// <para><b>Event publishing.</b> <see cref="QueueReordered"/> is runtime-
/// scoped (no single owning entity row) so we publish it directly through
/// <see cref="IMediator.Publish"/> instead of attaching it to a session via
/// <c>RaiseDomainEvent</c>. The
/// <c>DomainEventInterceptor</c> only collects events from tracked entities;
/// a manual publish is the right escape hatch for cross-entity events,
/// matching the <c>RegisterUserHandler</c> precedent for <c>UserCreated</c>.
/// </para>
/// </summary>
public record ReorderQueueCommand(
    Guid RuntimeId,
    IReadOnlyList<Guid> SessionIds,
    string ActorUserId
) : ICommand<Result<ReorderQueueResponse>>;

/// <summary>
/// Result shape for <see cref="ReorderQueueCommand"/>. <see cref="NewOrder"/>
/// echoes back the applied order so the frontend can confirm the server
/// agreed with the optimistic UI update — the position-1 session on this list
/// is the next to dispatch.
/// </summary>
public record ReorderQueueResponse(IReadOnlyList<Guid> NewOrder);

public class ReorderQueueCommandHandler : ICommandHandler<ReorderQueueCommand, Result<ReorderQueueResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly ILogger<ReorderQueueCommandHandler> _logger;

    public ReorderQueueCommandHandler(
        ApplicationDbContext db,
        IMediator mediator,
        ILogger<ReorderQueueCommandHandler> logger)
    {
        _db = db;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<ReorderQueueResponse>> Handle(
        ReorderQueueCommand request,
        CancellationToken cancellationToken)
    {
        // Load the current queue snapshot for the runtime: Pending sessions
        // with a non-null QueuePosition. We use IgnoreQueryFilters for the
        // same reason CancelSessionCommand does — a parent conversation that
        // was just archived must not hide a still-queued session the user is
        // about to reorder.
        var queued = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Where(s => s.RuntimeId == request.RuntimeId
                        && s.Status == AgentSessionStatus.Pending
                        && s.QueuePosition != null)
            .ToListAsync(cancellationToken);

        var existingIds = queued.Select(s => s.Id).ToHashSet();
        var requestIds = request.SessionIds.ToHashSet();

        // Set comparison: cardinality + membership in one shot. Duplicates in
        // the request list collapse in the HashSet, so requestIds.Count <
        // request.SessionIds.Count is also a mismatch — caught by the count
        // check below.
        if (requestIds.Count != request.SessionIds.Count
            || existingIds.Count != requestIds.Count
            || !existingIds.SetEquals(requestIds))
        {
            _logger.LogInformation(
                "ReorderQueue: mismatch on runtime {RuntimeId}. Existing=[{Existing}], requested=[{Requested}]; rejecting with refresh.",
                request.RuntimeId,
                string.Join(",", existingIds),
                string.Join(",", request.SessionIds));

            return Result.Failure<ReorderQueueResponse>("queue mismatch — refresh");
        }

        // Empty queue + empty request is a legitimate success: nothing to do,
        // nothing to fail. Skip the save + event so we don't write an empty
        // audit row.
        if (request.SessionIds.Count == 0)
        {
            return Result.Success(new ReorderQueueResponse(request.SessionIds));
        }

        // Renumber 1-based in the requested order. EF tracks every mutation;
        // a single SaveChanges flushes them as one Postgres batch.
        var byId = queued.ToDictionary(s => s.Id);
        for (var i = 0; i < request.SessionIds.Count; i++)
        {
            byId[request.SessionIds[i]].QueuePosition = i + 1;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Publish AFTER SaveChanges — the audit row should never claim a
        // reshuffle that didn't commit. Manual publish because QueueReordered
        // is runtime-scoped, not attached to any single AgentSession entity.
        await _mediator.Publish(
            new QueueReordered(request.RuntimeId, request.SessionIds, request.ActorUserId),
            cancellationToken);

        _logger.LogInformation(
            "ReorderQueue: runtime {RuntimeId} reordered to [{NewOrder}] by user {ActorUserId}.",
            request.RuntimeId,
            string.Join(",", request.SessionIds),
            request.ActorUserId);

        return Result.Success(new ReorderQueueResponse(request.SessionIds));
    }
}
