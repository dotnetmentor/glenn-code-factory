using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectTemplates.Models;
using Source.Features.ProjectTemplates.Queries.GetProjectTemplate;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.CreateProjectTemplate;

/// <summary>
/// Handler for <see cref="CreateProjectTemplateCommand"/>. Validates field
/// shape, verifies the (already-attribute-gated) caller is actually a
/// SuperAdmin, parses any inline <c>RuntimeSpec</c> against
/// <see cref="RuntimeSpecV3"/>, then inserts the row. Audit columns
/// (<c>CreatedAt</c> / <c>UpdatedAt</c>) and soft-delete defaults are stamped
/// by <see cref="ApplicationDbContext.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
public sealed class CreateProjectTemplateHandler
    : ICommandHandler<CreateProjectTemplateCommand, Result<ProjectTemplateDetail>>
{
    public const string NotAuthorizedError = "not_authorized";
    public const string InvalidNameError = "invalid_name";
    public const string InvalidSlugError = "invalid_slug";
    public const string InvalidDescriptionError = "invalid_description";
    public const string InvalidIconKeyError = "invalid_icon_key";
    public const string InvalidSourceRepoOwnerError = "invalid_source_repo_owner";
    public const string InvalidSourceRepoNameError = "invalid_source_repo_name";
    public const string SpecInvalidError = "spec_invalid";
    public const string NameTakenError = "name_taken";
    public const string SlugTakenError = "slug_taken";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<User> _userManager;

    public CreateProjectTemplateHandler(ApplicationDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<Result<ProjectTemplateDetail>> Handle(
        CreateProjectTemplateCommand request,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth — controller already has [Authorize(Roles=SuperAdmin)],
        // but re-check here so any future internal caller (jobs, console tools)
        // can't bypass the role gate.
        var authResult = await EnsureSuperAdminAsync(request.CallerUserId);
        if (authResult is not null) return authResult;

        // Field validation.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidNameError);
        }

        var slug = (request.Slug ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(slug) || slug.Length > 100)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidSlugError);
        }

        if (request.Description is not null && request.Description.Length > 500)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidDescriptionError);
        }

        if (request.IconKey is not null && request.IconKey.Length > 50)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidIconKeyError);
        }

        var sourceRepoOwner = (request.SourceRepoOwner ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(sourceRepoOwner) || sourceRepoOwner.Length > 120)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidSourceRepoOwnerError);
        }

        var sourceRepoName = (request.SourceRepoName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(sourceRepoName) || sourceRepoName.Length > 120)
        {
            return Result.Failure<ProjectTemplateDetail>(InvalidSourceRepoNameError);
        }

        // Inline RuntimeSpec — null means "Empty starter, runtime boots with the
        // default/empty recipe". Otherwise the JSON must parse against the V3
        // shape; structural validation (services non-empty, unique names) runs
        // through the same V3 <see cref="RuntimeSpecV3.Validate"/> the per-
        // runtime edit flow uses. Preset existence and parameter typing are
        // intentionally NOT checked here — the catalogue snapshot is opaque
        // text until project creation expands it through IPresetExpander.
        if (request.RuntimeSpec is not null)
        {
            var parsed = RuntimeSpecV3.TryParse(request.RuntimeSpec);
            if (parsed is null)
            {
                return Result.Failure<ProjectTemplateDetail>(SpecInvalidError);
            }
            var validate = parsed.Validate();
            if (!validate.IsSuccess)
            {
                return Result.Failure<ProjectTemplateDetail>(SpecInvalidError);
            }
        }

        // Uniqueness gates (active rows only — partial unique index on Slug
        // matches "non-tombstoned" semantics). Pre-check so we can return a
        // clean error instead of a generic DbUpdateException.
        var slugTaken = await _db.ProjectTemplates
            .AsNoTracking()
            .AnyAsync(t => t.Slug == slug, cancellationToken);
        if (slugTaken)
        {
            return Result.Failure<ProjectTemplateDetail>(SlugTakenError);
        }

        var nameTaken = await _db.ProjectTemplates
            .AsNoTracking()
            .AnyAsync(t => t.Name == name, cancellationToken);
        if (nameTaken)
        {
            return Result.Failure<ProjectTemplateDetail>(NameTakenError);
        }

        var entity = new ProjectTemplate
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            IconKey = string.IsNullOrWhiteSpace(request.IconKey) ? null : request.IconKey,
            SourceRepoOwner = sourceRepoOwner,
            SourceRepoName = sourceRepoName,
            RuntimeSpec = string.IsNullOrWhiteSpace(request.RuntimeSpec) ? null : request.RuntimeSpec,
            IsActive = request.IsActive,
            IsDefault = request.IsDefault,
            SortOrder = request.SortOrder,
            // CreatedAt / UpdatedAt are stamped by ApplicationDbContext.SaveChangesAsync.
        };

        _db.ProjectTemplates.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert lost the partial-unique-index race on Slug.
            return Result.Failure<ProjectTemplateDetail>(SlugTakenError);
        }

        return Result.Success(new ProjectTemplateDetail
        {
            Id = entity.Id,
            Slug = entity.Slug,
            Name = entity.Name,
            Description = entity.Description,
            IconKey = entity.IconKey,
            SourceRepoOwner = entity.SourceRepoOwner,
            SourceRepoName = entity.SourceRepoName,
            RuntimeSpec = entity.RuntimeSpec,
            IsActive = entity.IsActive,
            IsDefault = entity.IsDefault,
            SortOrder = entity.SortOrder,
            IsArchived = entity.IsDeleted,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        });
    }

    private async Task<Result<ProjectTemplateDetail>?> EnsureSuperAdminAsync(string callerUserId)
    {
        if (string.IsNullOrWhiteSpace(callerUserId))
        {
            return Result.Failure<ProjectTemplateDetail>(NotAuthorizedError);
        }

        var user = await _userManager.FindByIdAsync(callerUserId);
        if (user is null)
        {
            return Result.Failure<ProjectTemplateDetail>(NotAuthorizedError);
        }

        var isSuperAdmin = await _userManager.IsInRoleAsync(user, RoleConstants.SuperAdmin);
        if (!isSuperAdmin)
        {
            return Result.Failure<ProjectTemplateDetail>(NotAuthorizedError);
        }

        return null;
    }
}
