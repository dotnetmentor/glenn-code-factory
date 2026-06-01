using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.WorkspaceSpecs.Models;
using Source.Features.WorkspaceSpecs.Queries;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Commands;

/// <summary>
/// "Save current as catalog spec" — promote the owning project's <c>Spec</c>
/// into a named workspace catalog entry so the studio can reuse the same
/// stack on future branches / projects without re-authoring the JSON.
///
/// Spec lives on <see cref="Source.Features.Projects.Models.Project"/>, not
/// <c>ProjectRuntime</c> (per <c>project-level-runtime-spec</c>); the command
/// still keys off a <c>RuntimeId</c> for caller ergonomics (the studio drawer
/// knows the runtime, not the project), but the bytes it copies come from
/// the project row.
///
/// <list type="bullet">
///   <item>Looks up the <c>ProjectRuntime</c> by id (404 / <c>runtime_not_found</c>
///         if missing).</item>
///   <item>Authorises against the runtime's owning workspace
///         (<c>runtime.TenantId</c>) — same membership gate every other
///         <see cref="WorkspaceSpec"/> command uses. Non-members get
///         <c>not_a_member</c>, mapped to 403 by the controller.</item>
///   <item>Refuses to promote when the project's <c>Spec</c> is null/empty —
///         <c>runtime_has_no_spec</c>, 400 — there's nothing to save.</item>
///   <item>Defence-in-depth validation: re-runs the same V3 validator that
///         protects ordinary catalog writes against the project's current
///         <c>Spec</c>. A project that somehow ended up with an invalid spec
///         must not propagate that into the catalog.</item>
///   <item>Name uniqueness within the workspace is pre-checked then re-caught
///         from <see cref="DbUpdateException"/> on the unique index, matching
///         <see cref="CreateWorkspaceSpecHandler"/>.</item>
/// </list>
/// </summary>
public record SaveRuntimeSpecToCatalogCommand(
    Guid RuntimeId,
    string CallerUserId,
    string Name,
    string? Description
) : ICommand<Result<WorkspaceSpecDetail>>;

public sealed class SaveRuntimeSpecToCatalogHandler
    : ICommandHandler<SaveRuntimeSpecToCatalogCommand, Result<WorkspaceSpecDetail>>
{
    private readonly ApplicationDbContext _db;

    public SaveRuntimeSpecToCatalogHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSpecDetail>> Handle(
        SaveRuntimeSpecToCatalogCommand request,
        CancellationToken cancellationToken)
    {
        // 1) Load the runtime — we only need its TenantId + ProjectId to find
        //    the spec that now lives on Project (per `project-level-runtime-spec`).
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.Id == request.RuntimeId)
            .Select(r => new { r.TenantId, r.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("runtime_not_found");
        }

        // 2) Authorise against the runtime's owning workspace. TenantId carries
        //    the workspace boundary on ProjectRuntime — see RuntimeProvisionController.
        if (runtime.TenantId is null)
        {
            // A runtime without a tenant predates the workspace boundary; we
            // can't authorise it, so refuse rather than leak access.
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        var workspaceId = runtime.TenantId.Value;
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        // 3) Fetch the project's spec — the source of truth post-refactor.
        var projectSpec = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => p.Spec)
            .FirstOrDefaultAsync(cancellationToken);

        // Project must actually have a spec to promote. Empty-string is
        // treated the same as null — there's nothing meaningful to save.
        if (string.IsNullOrWhiteSpace(projectSpec))
        {
            return Result.Failure<WorkspaceSpecDetail>("runtime_has_no_spec");
        }

        // 4) Defence-in-depth: re-validate the project's spec via the V3
        //    validator. Don't propagate an invalid spec into the catalog.
        var parsed = RuntimeSpecV3.TryParse(projectSpec);
        if (parsed is null)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_spec_json");
        }
        var validate = parsed.Validate();
        if (!validate.IsSuccess)
        {
            return Result.Failure<WorkspaceSpecDetail>(validate.Error!);
        }

        // 5) Field validation on the new catalog entry's name / description.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_name");
        }

        if (request.Description is not null && request.Description.Length > 500)
        {
            return Result.Failure<WorkspaceSpecDetail>("invalid_description");
        }

        // 6) Name uniqueness within the workspace — pre-check + unique-index
        //    catch (same belt-and-suspenders shape as CreateWorkspaceSpec).
        var nameTaken = await _db.WorkspaceSpecs
            .AsNoTracking()
            .AnyAsync(
                s => s.WorkspaceId == workspaceId && s.Name == name,
                cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<WorkspaceSpecDetail>("name_taken");
        }

        // 7) Create the catalog entry, copying the project's Spec verbatim.
        var spec = new WorkspaceSpec
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            Content = projectSpec!,
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
            // Lost the unique-index race with a concurrent insert.
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
