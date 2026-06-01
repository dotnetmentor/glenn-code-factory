using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.DeletePreset;

/// <summary>
/// Soft-delete a preset. Built-ins are protected (return
/// <c>preset_built_in</c>); user clones are tombstoned via
/// <see cref="Models.ServicePreset.IsDeleted"/> so audit references (in-flight
/// proposals, expanded specs already persisted to <c>Projects.Spec</c>) still
/// resolve back to the row they were authored against.
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>preset_not_found</c> — id is missing or already soft-deleted.</item>
///   <item><c>preset_built_in</c> — built-in presets are not deletable.</item>
/// </list>
/// </summary>
public sealed record DeletePresetCommand(Guid Id) : ICommand<Result>;
