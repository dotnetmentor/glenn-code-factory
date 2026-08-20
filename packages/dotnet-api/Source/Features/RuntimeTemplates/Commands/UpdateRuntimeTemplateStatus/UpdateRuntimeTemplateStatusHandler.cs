using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTemplates.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeTemplates.Commands.UpdateRuntimeTemplateStatus;

/// <summary>
/// Handles <see cref="UpdateRuntimeTemplateStatusCommand"/>. Error shapes:
/// <list type="bullet">
///   <item><c>not_found</c> — no <see cref="RuntimeTemplate"/> with the given id;</item>
///   <item>already-in-status is a success no-op (idempotent guard so repeated UI
///         clicks don't churn the DB);</item>
///   <item>otherwise success, with the persisted row returned.</item>
/// </list>
///
/// <para>When promoting a row to <see cref="RuntimeTemplateStatus.Active"/> we first
/// demote every other Active row to <see cref="RuntimeTemplateStatus.Deprecated"/>.
/// The whole transition lands in a single <c>SaveChanges</c> so EF wraps it in an
/// implicit transaction — the "newest row with Status == Active" invariant
/// <c>RuntimeProvisionerJob</c> relies on stays uncorrupted under concurrency.</para>
/// </summary>
public sealed class UpdateRuntimeTemplateStatusHandler
    : ICommandHandler<UpdateRuntimeTemplateStatusCommand, Result<RuntimeTemplate>>
{
    public const string NotFoundError = "not_found";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<UpdateRuntimeTemplateStatusHandler> _logger;

    public UpdateRuntimeTemplateStatusHandler(
        ApplicationDbContext db,
        ILogger<UpdateRuntimeTemplateStatusHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<RuntimeTemplate>> Handle(
        UpdateRuntimeTemplateStatusCommand request,
        CancellationToken cancellationToken)
    {
        var template = await _db.RuntimeTemplates
            .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);

        if (template is null)
        {
            return Result.Failure<RuntimeTemplate>(NotFoundError);
        }

        if (template.Status == request.NewStatus)
        {
            // No-op — return the row unchanged so the controller can still emit a 200
            // (the user's intent has been met) without a write.
            return Result.Success(template);
        }

        if (request.NewStatus == RuntimeTemplateStatus.Active)
        {
            // Demote every other Active row first. Tracked via EF so they ride the same
            // SaveChanges call as the promotion below — single transaction.
            var others = await _db.RuntimeTemplates
                .Where(t => t.Status == RuntimeTemplateStatus.Active && t.Id != template.Id)
                .ToListAsync(cancellationToken);

            foreach (var o in others)
            {
                o.Status = RuntimeTemplateStatus.Deprecated;
            }

            if (others.Count > 0)
            {
                _logger.LogInformation(
                    "RuntimeTemplate activation: demoting {Count} previously-Active row(s) to Deprecated to preserve single-Active invariant.",
                    others.Count);
            }
        }

        template.Status = request.NewStatus;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "RuntimeTemplate {Id} ({Label}) status -> {Status}",
            template.Id, template.Label, template.Status);

        return Result.Success(template);
    }
}
