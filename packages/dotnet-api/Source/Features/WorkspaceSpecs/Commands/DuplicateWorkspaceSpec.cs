using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.WorkspaceSpecs.Models;
using Source.Features.WorkspaceSpecs.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Commands;

/// <summary>
/// Duplicate a catalog spec — copies <c>Content</c> verbatim into a new row
/// under a caller-supplied <see cref="NewName"/>. Useful for forking a
/// variant (e.g. clone <c>fullstack-dotnet-react</c> into
/// <c>fullstack-dotnet-react-with-redis</c>). The new row is independent
/// from the source: edits to either no longer affect the other.
/// </summary>
public record DuplicateWorkspaceSpecCommand(
    Guid WorkspaceId,
    Guid SourceSpecId,
    string CallerUserId,
    string NewName,
    string? NewDescription
) : ICommand<Result<WorkspaceSpecDetail>>;

public sealed class DuplicateWorkspaceSpecHandler
    : ICommandHandler<DuplicateWorkspaceSpecCommand, Result<WorkspaceSpecDetail>>
{
    private readonly ApplicationDbContext _db;

    public DuplicateWorkspaceSpecHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSpecDetail>> Handle(
        DuplicateWorkspaceSpecCommand request,
        CancellationToken cancellationToken)
    {
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        var newName = (request.NewName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName) || newName.Length > 100)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_name");
        }
        if (request.NewDescription is not null && request.NewDescription.Length > 500)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_description");
        }

        // Load source spec scoped to the URL workspace; cross-workspace id
        // would 404 (then 403 in the controller via the spec_not_found map).
        var source = await _db.WorkspaceSpecs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                s => s.Id == request.SourceSpecId && s.WorkspaceId == request.WorkspaceId,
                cancellationToken);
        if (source is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("spec_not_found");
        }

        // Re-validate the source content on duplicate. If a hand-edit somehow
        // poisoned the source (shouldn't happen — Create/Update both gate),
        // refuse to propagate the bad data through duplication.
        var parsed = RuntimeSpecV3.TryParse(source.Content);
        if (parsed is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_spec_json");
        }
        var validate = parsed.Validate();
        if (!validate.IsSuccess)
        {
            return Result.Failure<WorkspaceSpecDetail>(validate.Error!);
        }

        // New name must be unique within the workspace.
        var nameTaken = await _db.WorkspaceSpecs
            .AsNoTracking()
            .AnyAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.Name == newName,
                cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<WorkspaceSpecDetail>("name_taken");
        }

        var description = request.NewDescription is not null
            ? (string.IsNullOrWhiteSpace(request.NewDescription) ? null : request.NewDescription)
            : source.Description;

        var spec = new WorkspaceSpec
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            Name = newName,
            Description = description,
            Content = source.Content, // verbatim copy — snapshot semantic
            CreatedByUserId = request.CallerUserId,
            UpdatedByUserId = request.CallerUserId,
            // CreatedAt / UpdatedAt auto-set by IAuditable interceptor.
        };

        _db.WorkspaceSpecs.Add(spec);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<WorkspaceSpecDetail>("name_taken");
        }

        return Result.Success(new WorkspaceSpecDetail
        {
            Id = spec.Id,
            WorkspaceId = spec.WorkspaceId,
            Name = spec.Name,
            Description = spec.Description,
            Content = spec.Content,
            CreatedAt = spec.CreatedAt,
            UpdatedAt = spec.UpdatedAt,
            CreatedByUserId = spec.CreatedByUserId,
            UpdatedByUserId = spec.UpdatedByUserId,
        });
    }
}
