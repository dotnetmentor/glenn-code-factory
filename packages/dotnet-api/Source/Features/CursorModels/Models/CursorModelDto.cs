namespace Source.Features.CursorModels.Models;

/// <summary>
/// Wire shape for <see cref="CursorModel"/> read endpoints. Mirrors the
/// entity's user-visible fields verbatim, including the <c>@cursor/sdk</c>
/// variant / parameter metadata the frontend needs to render the picker's
/// per-model variant selector. Audit / soft-delete columns stay off the wire —
/// they're operator-internal.
/// </summary>
public record CursorModelDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string? Description,
    bool IsActive,
    List<string> Aliases,
    List<CursorModelParameter> Parameters,
    List<CursorModelVariant> Variants,
    int SortOrder);
