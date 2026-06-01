using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UnarchiveBranch;

/// <summary>
/// Handles <see cref="UnarchiveBranchCommand"/>. Loads the branch, calls
/// <c>Unarchive()</c> on the entity (idempotent + raises
/// <c>ProjectBranchUnarchived</c>), saves.
///
/// <para><b>Error shape.</b> 404 sentinel is the only failure mode the
/// command produces — the controller maps it to HTTP 404.</para>
/// </summary>
public sealed class UnarchiveBranchHandler : ICommandHandler<UnarchiveBranchCommand, Result>
{
    /// <summary>Stable error code the controller maps to HTTP 404.</summary>
    public const string NotFoundError = "not_found";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<UnarchiveBranchHandler> _logger;

    public UnarchiveBranchHandler(
        ApplicationDbContext db,
        ILogger<UnarchiveBranchHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result> Handle(UnarchiveBranchCommand request, CancellationToken cancellationToken)
    {
        var branch = await _db.ProjectBranches
            .FirstOrDefaultAsync(
                b => b.Id == request.BranchId && b.ProjectId == request.ProjectId,
                cancellationToken);

        if (branch is null)
        {
            return Result.Failure(NotFoundError);
        }

        // Idempotent — the entity short-circuits when already active, but
        // returning early here saves an unnecessary SaveChanges round-trip.
        if (!branch.IsArchived)
        {
            return Result.Success();
        }

        branch.Unarchive();
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "UnarchiveBranch: branch {BranchId} on project {ProjectId} restored.",
            branch.Id, branch.ProjectId);

        return Result.Success();
    }
}
