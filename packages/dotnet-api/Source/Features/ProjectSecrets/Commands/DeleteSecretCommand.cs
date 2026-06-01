using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Commands;

/// <summary>
/// Soft-delete a project secret. Flips <c>IsDeleted</c> on the row (the DbContext
/// stamps <c>DeletedAt</c> / <c>DeletedBy</c> via the <see cref="ISoftDelete"/>
/// interceptor), writes a <see cref="SecretAuditAction.Deleted"/> audit row in
/// the same transaction, and publishes <see cref="SecretsChanged"/> with
/// <c>Deleted = true</c> after commit so the Card 4 handler can tear down the
/// env-var on running daemons.
///
/// <para><b>Why soft delete:</b> the audit trail must outlive the row and the
/// downstream <see cref="SecretAuditEvent.SecretId"/> references stay valid.
/// The unique partial index on (ProjectId, Key) <c>WHERE DeletedAt IS NULL</c>
/// means the operator can re-add the same key after deletion without conflict.</para>
///
/// <para><b>Not-found:</b> a missing or already-deleted row returns
/// <c>"not_found"</c>; the controller maps that to 404. Idempotency on
/// already-deleted rows is delegated to the controller's 404 — repeating the
/// delete would log a redundant audit row otherwise.</para>
/// </summary>
public record DeleteSecretCommand(
    Guid ProjectId,
    string Key,
    string ActorUserId,
    Guid? BranchId = null) : ICommand<Result>;

public class DeleteSecretCommandHandler : ICommandHandler<DeleteSecretCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;

    public DeleteSecretCommandHandler(
        ApplicationDbContext db,
        IMediator mediator)
    {
        _db = db;
        _mediator = mediator;
    }

    public async Task<Result> Handle(
        DeleteSecretCommand request,
        CancellationToken cancellationToken)
    {
        var keyError = SecretValidation.ValidateKey(request.Key);
        if (keyError is not null)
        {
            return Result.Failure(keyError);
        }

        var existing = await _db.ProjectSecrets
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId
                  && s.BranchId == request.BranchId
                  && s.Key == request.Key,
                cancellationToken);

        if (existing is null)
        {
            return Result.Failure("not_found");
        }

        // Soft delete via the IsDeleted flag — DbContext.SaveChangesAsync
        // populates DeletedAt + DeletedBy through the ISoftDelete interceptor.
        // Do NOT call _db.ProjectSecrets.Remove(existing) — that's a hard
        // delete and would orphan the audit row's SecretId reference.
        existing.IsDeleted = true;

        // Write audit BEFORE SaveChanges so the row is in the same transaction
        // as the soft-delete flip. SecretId still points to the existing row;
        // the audit trail outlives the row even if it's later hard-deleted.
        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.Deleted,
            ProjectId = request.ProjectId,
            SecretId = existing.Id,
            SecretKey = existing.Key,
            Actor = request.ActorUserId,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);

        await _mediator.Publish(
            new SecretsChanged(request.ProjectId, request.Key, Deleted: true, request.BranchId),
            cancellationToken);

        return Result.Success();
    }
}
