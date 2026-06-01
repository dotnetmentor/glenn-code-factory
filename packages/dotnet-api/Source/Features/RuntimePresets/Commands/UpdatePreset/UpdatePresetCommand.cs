using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.UpdatePreset;

/// <summary>
/// Replace the mutable fields of an existing preset. Slug is immutable
/// post-create (it's the agent's tool-schema discriminator and the
/// <see cref="Services.PresetExpander"/>'s lookup key — changing it would
/// orphan in-flight proposals), so the command omits it.
///
/// <para><b>Built-in protection.</b> Rows with <c>IsBuiltIn=true</c> are
/// clone-only — the handler returns <c>preset_built_in</c> and the controller
/// maps that to 409 Conflict so the operator gets a clear "clone this first"
/// nudge in the UI.</para>
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>preset_not_found</c> — id is missing or soft-deleted.</item>
///   <item><c>preset_built_in</c> — caller tried to edit a seeded row.</item>
///   <item><c>display_name_invalid</c> / <c>description_invalid</c> — required field empty.</item>
///   <item><c>category_invalid</c> — category string doesn't parse to a <see cref="PresetCategory"/>.</item>
///   <item><c>command_template_invalid</c> — required field empty.</item>
/// </list>
/// </summary>
public sealed record UpdatePresetCommand(
    Guid Id,
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
