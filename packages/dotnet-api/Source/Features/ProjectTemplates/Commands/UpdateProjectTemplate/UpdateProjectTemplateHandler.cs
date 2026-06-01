using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectTemplates.Commands.CreateProjectTemplate;
using Source.Features.ProjectTemplates.Queries.GetProjectTemplate;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.UpdateProjectTemplate;

/// <summary>
/// Handler for <see cref="UpdateProjectTemplateCommand"/>. Loads the tracked
/// row (tombstoned rows are excluded by the global query filter), validates,
/// applies, saves. Error codes mirror
/// <see cref="CreateProjectTemplateHandler"/> with <c>template_not_found</c>
/// added for the lookup-miss case.
/// </summary>
public sealed class UpdateProjectTemplateHandler
    : ICommandHandler<UpdateProjectTemplateCommand, Result<ProjectTemplateDetail>>
{
    public const string NotFoundError = "template_not_found";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<User> _userManager;

    public UpdateProjectTemplateHandler(ApplicationDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<Result<ProjectTemplateDetail>> Handle(
        UpdateProjectTemplateCommand request,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth role check — mirror CreateProjectTemplateHandler.
        var authResult = await EnsureSuperAdminAsync(request.CallerUserId);
        if (authResult is not null) return authResult;

        // Field validation — share error codes with the create handler so the
        // frontend has one mapping table for both endpoints.
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidNameError);
        }

        var slug = (request.Slug ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(slug) || slug.Length > 100)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidSlugError);
        }

        if (request.Description is not null && request.Description.Length > 500)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidDescriptionError);
        }

        if (request.IconKey is not null && request.IconKey.Length > 50)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidIconKeyError);
        }

        var sourceRepoOwner = (request.SourceRepoOwner ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(sourceRepoOwner) || sourceRepoOwner.Length > 120)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidSourceRepoOwnerError);
        }

        var sourceRepoName = (request.SourceRepoName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(sourceRepoName) || sourceRepoName.Length > 120)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.InvalidSourceRepoNameError);
        }

        if (request.RuntimeSpec is not null)
        {
            // Mirror the Create handler: parse + structural-validate as V3.
            // Preset existence / parameter typing is deferred to project
            // creation (the IPresetExpander runs against the workspace's DB at
            // that point). See CreateProjectTemplateHandler for rationale.
            var parsed = RuntimeSpecV3.TryParse(request.RuntimeSpec);
            if (parsed is null)
            {
                return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.SpecInvalidError);
            }
            var validate = parsed.Validate();
            if (!validate.IsSuccess)
            {
                return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.SpecInvalidError);
            }
        }

        var entity = await _db.ProjectTemplates
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, cancellationToken);
        if (entity is null)
        {
            return Result.Failure<ProjectTemplateDetail>(NotFoundError);
        }

        // Uniqueness checks exclude the row being updated so an unchanged
        // name/slug doesn't collide with itself.
        if (!string.Equals(entity.Slug, slug, StringComparison.Ordinal))
        {
            var slugTaken = await _db.ProjectTemplates
                .AsNoTracking()
                .AnyAsync(t => t.Slug == slug && t.Id != request.TemplateId, cancellationToken);
            if (slugTaken)
            {
                return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.SlugTakenError);
            }
        }

        if (!string.Equals(entity.Name, name, StringComparison.Ordinal))
        {
            var nameTaken = await _db.ProjectTemplates
                .AsNoTracking()
                .AnyAsync(t => t.Name == name && t.Id != request.TemplateId, cancellationToken);
            if (nameTaken)
            {
                return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.NameTakenError);
            }
        }

        entity.Name = name;
        entity.Slug = slug;
        entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;
        entity.IconKey = string.IsNullOrWhiteSpace(request.IconKey) ? null : request.IconKey;
        entity.SourceRepoOwner = sourceRepoOwner;
        entity.SourceRepoName = sourceRepoName;
        entity.RuntimeSpec = string.IsNullOrWhiteSpace(request.RuntimeSpec) ? null : request.RuntimeSpec;
        entity.IsActive = request.IsActive;
        entity.IsDefault = request.IsDefault;
        entity.SortOrder = request.SortOrder;
        // UpdatedAt auto-bumped by ApplicationDbContext.SaveChangesAsync (IAuditable).

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.SlugTakenError);
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
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.NotAuthorizedError);
        }

        var user = await _userManager.FindByIdAsync(callerUserId);
        if (user is null)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.NotAuthorizedError);
        }

        var isSuperAdmin = await _userManager.IsInRoleAsync(user, RoleConstants.SuperAdmin);
        if (!isSuperAdmin)
        {
            return Result.Failure<ProjectTemplateDetail>(CreateProjectTemplateHandler.NotAuthorizedError);
        }

        return null;
    }
}
