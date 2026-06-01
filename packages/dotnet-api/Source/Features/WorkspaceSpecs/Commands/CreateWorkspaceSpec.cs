using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.WorkspaceSpecs.Models;
using Source.Features.WorkspaceSpecs.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Commands;

/// <summary>
/// Create a new workspace catalog spec. Validates:
/// <list type="bullet">
///   <item>Caller is a member of the target workspace (else <c>not_a_member</c>).</item>
///   <item>Name is non-empty, &lt;= 100 chars, unique within the workspace
///         (else <c>name_taken</c>, mapped to 409 by the controller).</item>
///   <item>Description, if provided, is &lt;= 500 chars.</item>
///   <item>Content parses + validates against the V3 RuntimeSpec validator —
///         the same gate that protects per-runtime spec edits.</item>
/// </list>
/// </summary>
public record CreateWorkspaceSpecCommand(
    Guid WorkspaceId,
    string CallerUserId,
    string Name,
    string? Description,
    string Content
) : ICommand<Result<WorkspaceSpecDetail>>;

public sealed class CreateWorkspaceSpecHandler
    : ICommandHandler<CreateWorkspaceSpecCommand, Result<WorkspaceSpecDetail>>
{
    private readonly ApplicationDbContext _db;

    public CreateWorkspaceSpecHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSpecDetail>> Handle(
        CreateWorkspaceSpecCommand request,
        CancellationToken cancellationToken)
    {
        // Authorization — must be a member of the target workspace.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        // Field validation.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_name");
        }

        if (request.Description is not null && request.Description.Length > 500)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_description");
        }

        // Validate spec content via the same V3 validator that protects per-runtime edits.
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

        // Pre-check name uniqueness within the workspace — gives a clean
        // error before we hit the unique-index race. We still catch DbUpdate-
        // Exception below to cover the (rare) concurrent-insert race.
        var nameTaken = await _db.WorkspaceSpecs
            .AsNoTracking()
            .AnyAsync(
                s => s.WorkspaceId == request.WorkspaceId && s.Name == name,
                cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<WorkspaceSpecDetail>("name_taken");
        }

        var spec = new WorkspaceSpec
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            Content = request.Content,
            CreatedByUserId = request.CallerUserId,
            UpdatedByUserId = request.CallerUserId,
            // CreatedAt / UpdatedAt are auto-set by ApplicationDbContext.SaveChangesAsync (IAuditable).
        };

        _db.WorkspaceSpecs.Add(spec);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert with the same (WorkspaceId, Name) lost the race.
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
