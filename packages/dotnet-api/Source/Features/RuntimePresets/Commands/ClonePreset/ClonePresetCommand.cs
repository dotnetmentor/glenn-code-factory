using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.ClonePreset;

/// <summary>
/// Duplicate an existing preset under a new slug — the primary way operators
/// customise a built-in preset (built-ins are clone-only). All fields are
/// copied verbatim, <c>IsBuiltIn</c> is forced to <c>false</c>, and a fresh
/// <c>Id</c> is assigned.
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>preset_not_found</c> — source id is missing or soft-deleted.</item>
///   <item><c>slug_invalid</c> — new slug fails the format regex.</item>
///   <item><c>slug_taken</c> — another row (incl. soft-deleted) already uses this slug.</item>
/// </list>
/// </summary>
public sealed record ClonePresetCommand(
    Guid SourceId,
    string NewSlug,
    string? NewDisplayName
) : ICommand<Result<ServicePresetDto>>;
