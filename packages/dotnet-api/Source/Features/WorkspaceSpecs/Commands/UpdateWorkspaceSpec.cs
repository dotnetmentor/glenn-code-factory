using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.WorkspaceSpecs.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Commands;

/// <summary>
/// Update a catalog spec's name, description, and content. Overwrites in
/// place — there is no version history. Edits NEVER touch existing branches
/// that were previously forked from this spec (snapshot semantic).
/// </summary>
public record UpdateWorkspaceSpecCommand(
    Guid WorkspaceId,
    Guid SpecId,
    string CallerUserId,
    string Name,
    string? Description,
    string Content
) : ICommand<Result<WorkspaceSpecDetail>>;

public sealed class UpdateWorkspaceSpecHandler
    : ICommandHandler<UpdateWorkspaceSpecCommand, Result<WorkspaceSpecDetail>>
{
    private readonly ApplicationDbContext _db;

    public UpdateWorkspaceSpecHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSpecDetail>> Handle(
        UpdateWorkspaceSpecCommand request,
        CancellationToken cancellationToken)
    {
        // Authorization — must be a member of the URL workspace.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        // Validate inputs before we hit the DB.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_name");
        }
        if (request.Description is not null && request.Description.Length > 500)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_description");
        }

        var parsed = RuntimeSpecV3.TryParse(request.Content);
        if (parsed is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_spec_json");
        }
        var validate = parsed.Validate();
        if (!validate.IsSuccess)
        {
            return Result.Failure<WorkspaceSpecDetail>(validate.Error!);
        }

        // Find spec scoped to URL workspace — cross-workspace id lookups
        // collapse to not_a_member (same 403 response as a non-member would
        // get) to avoid leaking existence of specs in other workspaces.
        var spec = await _db.WorkspaceSpecs
            .SingleOrDefaultAsync(
                s => s.Id == request.SpecId && s.WorkspaceId == request.WorkspaceId,
                cancellationToken);
        if (spec is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("spec_not_found");
        }

        // Pre-check uniqueness for the new name within the workspace, excluding
        // the row we're updating.
        if (!string.Equals(spec.Name, name, StringComparison.Ordinal))
        {
            var nameTaken = await _db.WorkspaceSpecs
                .AsNoTracking()
                .AnyAsync(
                    s => s.WorkspaceId == request.WorkspaceId
                         && s.Id != request.SpecId
                         && s.Name == name,
                    cancellationToken);
            if (nameTaken)
            {
                return Result.Failure<WorkspaceSpecDetail>("name_taken");
            }
        }

        spec.Name = name;
        spec.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;
        spec.Content = request.Content;
        spec.UpdatedByUserId = request.CallerUserId;
        // UpdatedAt auto-bumped by ApplicationDbContext.SaveChangesAsync (IAuditable).
        // CreatedAt / CreatedByUserId are intentionally untouched.

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
