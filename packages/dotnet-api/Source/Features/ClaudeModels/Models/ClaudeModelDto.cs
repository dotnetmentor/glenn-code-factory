namespace Source.Features.ClaudeModels.Models;

/// <summary>
/// Wire shape for <see cref="ClaudeModel"/> read endpoints. Mirrors the
/// entity's user-visible fields verbatim, including the reasoning metadata the
/// composer needs to decide whether to render the reasoning-effort dropdown
/// (<see cref="SupportsReasoning"/>) and what to pre-select
/// (<see cref="DefaultEffort"/>). Audit / soft-delete columns stay off the wire
/// — they're operator-internal.
/// </summary>
public record ClaudeModelDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string? Description,
    bool IsSystemDefault,
    bool SupportsReasoning,
    string? DefaultEffort,
    bool IsActive,
    int SortOrder);
