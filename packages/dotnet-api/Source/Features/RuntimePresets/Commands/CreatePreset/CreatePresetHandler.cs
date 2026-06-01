using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.CreatePreset;

/// <summary>
/// Handler for <see cref="CreatePresetCommand"/>. Validates field shapes,
/// checks slug uniqueness against the entire table (including soft-deleted
/// rows — re-using a tombstoned slug would silently collide once the soft
/// delete is reaped or the partial unique index is recomputed), then inserts
/// the row. Audit columns (<c>CreatedAt</c>, <c>UpdatedAt</c>) and soft-delete
/// defaults are stamped by
/// <see cref="ApplicationDbContext.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
public sealed class CreatePresetHandler
    : ICommandHandler<CreatePresetCommand, Result<ServicePresetDto>>
{
    public const string SlugInvalidError = "slug_invalid";
    public const string SlugTakenError = "slug_taken";
    public const string DisplayNameInvalidError = "display_name_invalid";
    public const string DescriptionInvalidError = "description_invalid";
    public const string CategoryInvalidError = "category_invalid";
    public const string CommandTemplateInvalidError = "command_template_invalid";

    /// <summary>
    /// Slug regex — lower-kebab, must start with a letter, may contain letters,
    /// digits, hyphens. Matches the convention used by the seed presets
    /// (<c>dotnet-mise</c>, <c>node-vite</c>, <c>postgres-15</c>) and the URL-
    /// safety expectations of the agent tool schema's <c>oneOf</c> branches.
    /// </summary>
    private static readonly Regex SlugRegex = new(
        @"^[a-z][a-z0-9-]+$",
        RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;

    public CreatePresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ServicePresetDto>> Handle(
        CreatePresetCommand request,
        CancellationToken cancellationToken)
    {
        var slug = (request.Slug ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(slug) || slug.Length > 64 || !SlugRegex.IsMatch(slug))
        {
            return Result.Failure<ServicePresetDto>(SlugInvalidError);
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 128)
        {
            return Result.Failure<ServicePresetDto>(DisplayNameInvalidError);
        }

        var description = (request.Description ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(description) || description.Length > 1024)
        {
            return Result.Failure<ServicePresetDto>(DescriptionInvalidError);
        }

        if (!Enum.TryParse<PresetCategory>(request.Category, ignoreCase: true, out var category))
        {
            return Result.Failure<ServicePresetDto>(CategoryInvalidError);
        }

        var commandTemplate = (request.CommandTemplate ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(commandTemplate) || commandTemplate.Length > 4096)
        {
            return Result.Failure<ServicePresetDto>(CommandTemplateInvalidError);
        }

        // Uniqueness against the WHOLE table (lift the soft-delete filter).
        // The DB-level unique index is on Slug full stop — a tombstoned row
        // with the same slug would block the insert, so we need to surface a
        // clean error rather than DbUpdateException.
        var slugTaken = await _db.ServicePresets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(p => p.Slug == slug, cancellationToken);
        if (slugTaken)
        {
            return Result.Failure<ServicePresetDto>(SlugTakenError);
        }

        // Serialize EnvTemplate / Parameters with PresetParameter.JsonOptions
        // so the on-disk shape matches the migration seed: camelCase keys,
        // string enums. Drift here means the expander deserialise blows up at
        // boot — same options on both sides eliminates the class.
        var envJson = JsonSerializer.Serialize(
            request.EnvTemplate ?? new Dictionary<string, string>(),
            PresetParameter.JsonOptions);
        var parametersJson = JsonSerializer.Serialize(
            request.Parameters ?? new List<PresetParameter>(),
            PresetParameter.JsonOptions);

        var entity = new ServicePreset
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = displayName,
            Description = description,
            Category = category,
            IconName = string.IsNullOrWhiteSpace(request.IconName) ? null : request.IconName.Trim(),
            // Admin-created presets are always user clones — built-in is migration-only.
            IsBuiltIn = false,
            CommandTemplate = commandTemplate,
            EnvTemplate = envJson,
            HealthcheckCommand = string.IsNullOrWhiteSpace(request.HealthcheckCommand)
                ? null
                : request.HealthcheckCommand.Trim(),
            HealthcheckInterval = request.HealthcheckInterval,
            DefaultUser = string.IsNullOrWhiteSpace(request.DefaultUser) ? null : request.DefaultUser.Trim(),
            Autorestart = request.Autorestart,
            InstallContribution = string.IsNullOrWhiteSpace(request.InstallContribution)
                ? null
                : request.InstallContribution,
            SetupContribution = string.IsNullOrWhiteSpace(request.SetupContribution)
                ? null
                : request.SetupContribution,
            InstallVerify = string.IsNullOrWhiteSpace(request.InstallVerify)
                ? null
                : request.InstallVerify,
            Parameters = parametersJson,
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
