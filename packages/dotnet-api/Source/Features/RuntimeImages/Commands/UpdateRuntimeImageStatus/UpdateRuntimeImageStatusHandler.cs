using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeImages.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeImages.Commands.UpdateRuntimeImageStatus;

/// <summary>
/// Handles <see cref="UpdateRuntimeImageStatusCommand"/>. Three error shapes:
/// <list type="bullet">
///   <item><c>not_found</c> — no <see cref="RuntimeImage"/> with the given id;</item>
///   <item><c>noop</c> — the row is already in the requested status (idempotent guard so
///         repeated UI clicks don't churn the DB);</item>
///   <item>otherwise success, with the persisted row returned.</item>
/// </list>
///
/// <para>When promoting a row to <see cref="RuntimeImageStatus.Active"/> we first demote
/// every other Active row to <see cref="RuntimeImageStatus.Deprecated"/>. The whole
/// transition lands in a single <c>SaveChanges</c> so EF wraps it in an implicit
/// transaction — either every demotion lands together with the promotion, or none of
/// them do. That keeps the "newest row with Status == Active" invariant
/// <c>RuntimeProvisionerJob</c> relies on uncorrupted under concurrency.</para>
/// </summary>
public sealed class UpdateRuntimeImageStatusHandler
    : ICommandHandler<UpdateRuntimeImageStatusCommand, Result<RuntimeImage>>
{
    public const string NotFoundError = "not_found";
    public const string NoopError = "noop";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<UpdateRuntimeImageStatusHandler> _logger;

    public UpdateRuntimeImageStatusHandler(
        ApplicationDbContext db,
        ILogger<UpdateRuntimeImageStatusHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<RuntimeImage>> Handle(
        UpdateRuntimeImageStatusCommand request,
        CancellationToken cancellationToken)
    {
        var image = await _db.RuntimeImages
            .FirstOrDefaultAsync(i => i.Id == request.Id, cancellationToken);

        if (image is null)
        {
            return Result.Failure<RuntimeImage>(NotFoundError);
        }

        if (image.Status == request.NewStatus)
        {
            // No-op — return the row unchanged so the controller can still emit a 200
            // (the user's intent has been met) without a write.
            return Result.Success(image);
        }

        if (request.NewStatus == RuntimeImageStatus.Active)
        {
            // Demote every other Active row first. Tracked via EF so they ride the same
            // SaveChanges call as the promotion below — single transaction.
            var others = await _db.RuntimeImages
                .Where(i => i.Status == RuntimeImageStatus.Active && i.Id != image.Id)
                .ToListAsync(cancellationToken);

            foreach (var o in others)
            {
                o.Status = RuntimeImageStatus.Deprecated;
            }

            if (others.Count > 0)
            {
                _logger.LogInformation(
                    "RuntimeImage activation: demoting {Count} previously-Active row(s) to Deprecated to preserve single-Active invariant.",
                    others.Count);
            }
        }

        image.Status = request.NewStatus;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "RuntimeImage status updated: id={Id}, tag={Tag}, status={Status}",
            image.Id, image.Tag, image.Status);

        return Result.Success(image);
    }
}
