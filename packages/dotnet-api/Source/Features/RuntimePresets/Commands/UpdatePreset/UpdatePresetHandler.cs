using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.UpdatePreset;

/// <summary>
/// Handler for <see cref="UpdatePresetCommand"/>. Loads the tracked row,
/// rejects built-ins, validates inputs, copies onto the entity and saves.
/// </summary>
public sealed class UpdatePresetHandler
    : ICommandHandler<UpdatePresetCommand, Result<ServicePresetDto>>
{
    public const string NotFoundError = "preset_not_found";
    public const string BuiltInError = "preset_built_in";
    public const string DisplayNameInvalidError = "display_name_invalid";
    public const string DescriptionInvalidError = "description_invalid";
    public const string CategoryInvalidError = "category_invalid";
    public const string CommandTemplateInvalidError = "command_template_invalid";

    private readonly ApplicationDbContext _db;

    public UpdatePresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ServicePresetDto>> Handle(
        UpdatePresetCommand request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ServicePresets
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (entity is null)
        {
            return Result.Failure<ServicePresetDto>(NotFoundError);
        }

        if (entity.IsBuiltIn)
        {
            return Result.Failure<ServicePresetDto>(BuiltInError);
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

        var envJson = JsonSerializer.Serialize(
            request.EnvTemplate ?? new Dictionary<string, string>(),
            PresetParameter.JsonOptions);
        var parametersJson = JsonSerializer.Serialize(
            request.Parameters ?? new List<PresetParameter>(),
            PresetParameter.JsonOptions);

        entity.DisplayName = displayName;
        entity.Description = description;
        entity.Category = category;
        entity.IconName = string.IsNullOrWhiteSpace(request.IconName) ? null : request.IconName.Trim();
        entity.CommandTemplate = commandTemplate;
        entity.EnvTemplate = envJson;
        entity.HealthcheckCommand = string.IsNullOrWhiteSpace(request.HealthcheckCommand)
            ? null
            : request.HealthcheckCommand.Trim();
        entity.HealthcheckInterval = request.HealthcheckInterval;
        entity.DefaultUser = string.IsNullOrWhiteSpace(request.DefaultUser) ? null : request.DefaultUser.Trim();
        entity.Autorestart = request.Autorestart;
        entity.InstallContribution = string.IsNullOrWhiteSpace(request.InstallContribution)
            ? null
            : request.InstallContribution;
        entity.SetupContribution = string.IsNullOrWhiteSpace(request.SetupContribution)
            ? null
            : request.SetupContribution;
        entity.InstallVerify = string.IsNullOrWhiteSpace(request.InstallVerify)
            ? null
            : request.InstallVerify;
        entity.Parameters = parametersJson;
        // UpdatedAt auto-bumped by ApplicationDbContext.SaveChangesAsync (IAuditable).

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(PresetDtoMapper.ToDto(entity));
    }
}
