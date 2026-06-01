using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Commands.CreatePreset;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.ClonePreset;

/// <summary>
/// Handler for <see cref="ClonePresetCommand"/>. Loads the source row,
/// validates the new slug, copies every field verbatim onto a fresh entity
/// (<c>IsBuiltIn=false</c>, new <c>Id</c>), and saves.
/// </summary>
public sealed class ClonePresetHandler
    : ICommandHandler<ClonePresetCommand, Result<ServicePresetDto>>
{
    public const string NotFoundError = "preset_not_found";
    public const string SlugInvalidError = "slug_invalid";
    public const string SlugTakenError = "slug_taken";

    /// <summary>
    /// Reuses the same regex as <see cref="CreatePresetHandler"/> — kept local
    /// instead of factored out because the validation rules deliberately
    /// belong to the command, not a shared utility (the spec authors may
    /// later loosen one without the other).
    /// </summary>
    private static readonly Regex SlugRegex = new(
        @"^[a-z][a-z0-9-]+$",
        RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;

    public ClonePresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ServicePresetDto>> Handle(
        ClonePresetCommand request,
        CancellationToken cancellationToken)
    {
        var source = await _db.ServicePresets
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.SourceId, cancellationToken);
        if (source is null)
        {
            return Result.Failure<ServicePresetDto>(NotFoundError);
        }

        var newSlug = (request.NewSlug ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newSlug) || newSlug.Length > 64 || !SlugRegex.IsMatch(newSlug))
        {
            return Result.Failure<ServicePresetDto>(SlugInvalidError);
        }

        var slugTaken = await _db.ServicePresets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(p => p.Slug == newSlug, cancellationToken);
        if (slugTaken)
        {
            return Result.Failure<ServicePresetDto>(SlugTakenError);
        }

        // Default-derive the display name when the caller didn't supply one —
        // mirrors the "(copy)" convention every other clone-style UI uses.
        var newDisplayName = string.IsNullOrWhiteSpace(request.NewDisplayName)
            ? $"{source.DisplayName} (copy)"
            : request.NewDisplayName.Trim();
        if (newDisplayName.Length > 128)
        {
            newDisplayName = newDisplayName[..128];
        }

        var entity = new ServicePreset
        {
            Id = Guid.NewGuid(),
            Slug = newSlug,
            DisplayName = newDisplayName,
            Description = source.Description,
            Category = source.Category,
            IconName = source.IconName,
            // Clones are never built-in — that's the whole point of cloning.
            IsBuiltIn = false,
            CommandTemplate = source.CommandTemplate,
            EnvTemplate = source.EnvTemplate,
            HealthcheckCommand = source.HealthcheckCommand,
            HealthcheckInterval = source.HealthcheckInterval,
            DefaultUser = source.DefaultUser,
            Autorestart = source.Autorestart,
            InstallContribution = source.InstallContribution,
            SetupContribution = source.SetupContribution,
            InstallVerify = source.InstallVerify,
            Parameters = source.Parameters,
            // CreatedAt / UpdatedAt stamped by SaveChangesAsync.
        };

        _db.ServicePresets.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert lost the unique-index race.
            return Result.Failure<ServicePresetDto>(SlugTakenError);
        }

        return Result.Success(PresetDtoMapper.ToDto(entity));
    }
}
