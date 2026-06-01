using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.CreatePreset;

/// <summary>
/// Create a new user preset. Admins always create with <c>IsBuiltIn = false</c>
/// — the built-in flag is migration-only because flipping it carries
/// semantic weight (the gallery hides edit / delete on built-ins).
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>slug_invalid</c> — slug fails the <c>^[a-z][a-z0-9-]+$</c> regex.</item>
///   <item><c>slug_taken</c> — another row (including soft-deleted) already uses this slug.</item>
///   <item><c>display_name_invalid</c> / <c>description_invalid</c> — required field empty.</item>
///   <item><c>category_invalid</c> — category string doesn't parse to a <see cref="PresetCategory"/>.</item>
///   <item><c>command_template_invalid</c> — required field empty.</item>
/// </list>
/// </summary>
public sealed record CreatePresetCommand(
    string Slug,
    string DisplayName,
    string Description,
    string Category,
    string? IconName,
    string CommandTemplate,
    Dictionary<string, string>? EnvTemplate,
    string? HealthcheckCommand,
    int? HealthcheckInterval,
    string? DefaultUser,
    bool Autorestart,
    string? InstallContribution,
    string? SetupContribution,
    string? InstallVerify,
    List<PresetParameter>? Parameters
) : ICommand<Result<ServicePresetDto>>;
