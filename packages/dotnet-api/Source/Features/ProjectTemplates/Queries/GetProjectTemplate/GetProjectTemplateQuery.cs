using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Queries.GetProjectTemplate;

/// <summary>
/// Fetch one <see cref="Models.ProjectTemplate"/> by id with the full inline
/// <c>RuntimeSpec</c> JSON document. Backs the admin edit screen
/// (super-admin gated at the controller) and any future "starter detail" view.
///
/// <para>Returns <c>template_not_found</c> when the id is missing or tombstoned —
/// admin paths that need to view archived rows should lift the filter at the
/// controller layer or via a dedicated query (this one honours the global
/// soft-delete filter).</para>
/// </summary>
public sealed record GetProjectTemplateQuery(Guid TemplateId, string CallerUserId)
    : IQuery<Result<ProjectTemplateDetail>>;

/// <summary>
/// Full detail shape for a single starter. Includes the raw
/// <see cref="RuntimeSpec"/> JSON document (null when the starter has no
/// curated recipe — the "Empty" seed and any starter saved without one).
/// </summary>
public sealed record ProjectTemplateDetail
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? IconKey { get; init; }
    public required string SourceRepoOwner { get; init; }
    public required string SourceRepoName { get; init; }
    public string? RuntimeSpec { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsDefault { get; init; }
    public required int SortOrder { get; init; }
    public required bool IsArchived { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
