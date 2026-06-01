using Microsoft.EntityFrameworkCore;
using Source.Features.Specifications.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;
using Tapper;

namespace Source.Features.Specifications.Commands;

/// <summary>
/// Idempotent upsert of a spec, keyed on <c>(ProjectId, Slug)</c>. If a
/// non-deleted spec with that slug exists, its name/content are updated;
/// otherwise a fresh row is created. The MCP <c>save_specification</c> tool
/// and the REST <c>PUT /api/projects/{id}/specifications/{slug}</c> endpoint
/// both dispatch this — neither cares about create-vs-update, just "make the
/// spec look like this".
///
/// <para><b>Slug uniqueness with soft-delete.</b> The unique index is filtered
/// to <c>IsDeleted = false</c>, so the slug becomes available again after
/// delete. We deliberately do NOT resurrect the soft-deleted row — a new
/// <see cref="Specification.Create"/> call mints a new <see cref="Specification.Id"/>
/// (and a fresh <see cref="Events.SpecificationCreated"/>), keeping the audit
/// trail honest about "this is a new spec under the same slug".</para>
///
/// <para><b>CreatedBy semantics.</b> Identity user ids satisfy the FK to
/// <c>AspNetUsers</c>; the daemon's runtime token doesn't have one, so the
/// MCP controller passes <c>null</c> and the runtime actor is captured in the
/// <c>McpCall</c> audit row by the framework. The REST controller passes the
/// signed-in user's id.</para>
/// </summary>
public record SaveSpecificationCommand(
    Guid ProjectId,
    string Slug,
    string Name,
    string Content,
    string? CreatedBy) : ICommand<Result<SaveSpecificationResponse>>;

/// <summary>
/// Result payload for <see cref="SaveSpecificationCommand"/>. <see cref="Created"/>
/// tells the caller which branch ran (helpful for the REST controller to pick
/// 201 vs 200, and for the daemon to log "created N, updated M" summaries).
/// </summary>
[TranspilationSource]
public record SaveSpecificationResponse(
    Guid Id,
    string Slug,
    bool Created);

public class SaveSpecificationCommandHandler
    : ICommandHandler<SaveSpecificationCommand, Result<SaveSpecificationResponse>>
{
    private readonly ApplicationDbContext _db;

    public SaveSpecificationCommandHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<SaveSpecificationResponse>> Handle(
        SaveSpecificationCommand request,
        CancellationToken cancellationToken)
    {
        // Cheap up-front validation so a bad call never reaches Specification.Create
        // (which throws on invalid slug/name — that's a programmer-error path,
        // not an expected-failure path). The Result-shaped errors here are the
        // contract clients see.
        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            return Result.Failure<SaveSpecificationResponse>("invalid_slug");
        }

        var slug = request.Slug.Trim().ToLowerInvariant();

        if (slug.Length > Specification.MaxSlugLength)
        {
            return Result.Failure<SaveSpecificationResponse>("invalid_slug");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Result.Failure<SaveSpecificationResponse>("invalid_name");
        }

        if (request.Name.Length > Specification.MaxNameLength)
        {
            return Result.Failure<SaveSpecificationResponse>("invalid_name");
        }

        // Project filter on the lookup is defence-in-depth — the global query
        // filter on Specification already hides soft-deleted rows, so a slug
        // that was previously deleted is invisible here and the Create branch
        // runs cleanly (the filtered unique index permits it).
        var existing = await _db.Specifications
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId && s.Slug == slug,
                cancellationToken);

        if (existing is not null)
        {
            var updateResult = existing.UpdateContent(request.Name, request.Content ?? string.Empty);
            if (updateResult.IsFailure)
            {
                return Result.Failure<SaveSpecificationResponse>(updateResult.Error!);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return Result.Success(new SaveSpecificationResponse(existing.Id, existing.Slug, Created: false));
        }

        Specification spec;
        try
        {
            spec = Specification.Create(
                request.ProjectId,
                slug,
                request.Name,
                request.Content ?? string.Empty,
                request.CreatedBy);
        }
        catch (ArgumentException)
        {
            // The entity-level guards re-validate slug shape; the up-front
            // checks above cover the common cases but the pattern regex on the
            // entity is the source of truth for "is this a valid slug?".
            return Result.Failure<SaveSpecificationResponse>("invalid_slug");
        }

        _db.Specifications.Add(spec);
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new SaveSpecificationResponse(spec.Id, spec.Slug, Created: true));
    }
}
