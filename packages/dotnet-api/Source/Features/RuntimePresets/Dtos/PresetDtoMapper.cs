using System.Text.Json;
using Source.Features.RuntimePresets.Models;

namespace Source.Features.RuntimePresets.Dtos;

/// <summary>
/// Centralises entity → DTO mapping so every query / command handler returns
/// the same shape. The mapping is non-trivial because two columns
/// (<c>EnvTemplate</c>, <c>Parameters</c>) are raw JSON strings on the entity
/// and structured objects on the wire — duplicating the deserialise logic
/// across five handlers would invite drift.
/// </summary>
public static class PresetDtoMapper
{
    /// <summary>
    /// Project a <see cref="ServicePreset"/> row into its wire shape. Tolerates
    /// null / empty <c>EnvTemplate</c> (renders as empty dict) and any
    /// well-formed <c>Parameters</c> JSON (renders via
    /// <see cref="PresetParameter.DeserializeList"/>, which itself tolerates
    /// nulls).
    /// </summary>
    public static ServicePresetDto ToDto(ServicePreset entity)
    {
        var envTemplate = string.IsNullOrWhiteSpace(entity.EnvTemplate)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(entity.EnvTemplate)
              ?? new Dictionary<string, string>();

        var parameters = PresetParameter.DeserializeList(entity.Parameters);

        return new ServicePresetDto(
            Id: entity.Id,
            Slug: entity.Slug,
            DisplayName: entity.DisplayName,
            Description: entity.Description,
            Category: entity.Category.ToString(),
            IconName: entity.IconName,
            IsBuiltIn: entity.IsBuiltIn,
            CommandTemplate: entity.CommandTemplate,
            EnvTemplate: envTemplate,
            HealthcheckCommand: entity.HealthcheckCommand,
            HealthcheckInterval: entity.HealthcheckInterval,
            DefaultUser: entity.DefaultUser,
            Autorestart: entity.Autorestart,
            InstallContribution: entity.InstallContribution,
            SetupContribution: entity.SetupContribution,
            InstallVerify: entity.InstallVerify,
            Parameters: parameters,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt);
    }
}
