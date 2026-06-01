using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Specifications.Commands;

/// <summary>
/// Soft-delete a spec keyed on <c>(ProjectId, Slug)</c>. Calls
/// <see cref="Models.Specification.MarkDeleted"/> on the entity which flips
/// <c>IsDeleted</c> and raises <see cref="Events.SpecificationDeleted"/>;
/// SaveChanges then stamps <c>DeletedAt</c> / <c>DeletedBy</c> via the
/// <c>ISoftDelete</c> interceptor.
///
/// <para><b>Project scope + not-found uniformity.</b> Cross-project / unknown
/// slug lookups return <c>"not_found"</c> rather than <c>"forbidden"</c> —
/// same convention as the rest of the planning slice and the kanban slice.</para>
/// </summary>
public record DeleteSpecificationCommand(
    Guid ProjectId,
    string Slug) : ICommand<Result>;

public class DeleteSpecificationCommandHandler
    : ICommandHandler<DeleteSpecificationCommand, Result>
{
    private readonly ApplicationDbContext _db;

    public DeleteSpecificationCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result> Handle(
        DeleteSpecificationCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            return Result.Failure("not_found");
        }

        var slug = request.Slug.Trim().ToLowerInvariant();

        // The global query filter hides already-deleted rows, so a second
        // delete on the same slug naturally returns "not_found".
        var spec = await _db.Specifications
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId && s.Slug == slug,
                cancellationToken);

        if (spec is null)
        {
            return Result.Failure("not_found");
        }

        var result = spec.MarkDeleted();
        if (result.IsFailure)
        {
            return result;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
