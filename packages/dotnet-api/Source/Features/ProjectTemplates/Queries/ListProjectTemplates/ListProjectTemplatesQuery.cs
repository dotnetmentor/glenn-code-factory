using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Queries.ListProjectTemplates;

/// <summary>
/// List every <see cref="Models.ProjectTemplate"/> in the curated global catalogue.
///
/// <para><b>Two callers.</b> The admin "Manage Starters" page passes
/// <see cref="IncludeArchived"/> = <c>true</c> so the table can show + restore
/// soft-deleted rows. The user-facing new-project picker passes
/// <c>false</c> and additionally filters to <c>IsActive</c> rows in the picker
/// route (the handler honours <c>IncludeArchived</c>, the picker controller
/// post-filters on <c>IsActive</c>).</para>
///
/// <para><b>Ordering.</b> Always <see cref="Models.ProjectTemplate.SortOrder"/>
/// ASC then <see cref="Models.ProjectTemplate.Name"/> ASC, identical to the
/// indexes defined on the table for picker performance.</para>
///
/// <para><b>Authorisation.</b> Gated at the controller via
/// <c>[Authorize(Roles = SuperAdmin)]</c> on the admin route; the picker route
/// is open to any authenticated caller. <see cref="CallerUserId"/> is captured
/// for telemetry / future per-caller filtering but is not currently used to
/// gate the query — controller-level role attributes are the source of truth.</para>
/// </summary>
public sealed record ListProjectTemplatesQuery(
    string CallerUserId,
    bool IncludeArchived = false
) : IQuery<Result<List<ProjectTemplateListItem>>>;

/// <summary>
/// Lightweight list row for the picker + admin table. Omits the (potentially
/// large) <c>RuntimeSpec</c> jsonb payload and exposes <see cref="HasRuntimeSpec"/>
/// instead — the admin table just needs "is this starter wired with a recipe?"
/// and the picker doesn't need the recipe at all. The detail endpoint
/// (<see cref="GetProjectTemplate.GetProjectTemplateQuery"/>) returns the full
/// document for the edit screen.
/// </summary>
public sealed record ProjectTemplateListItem
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? IconKey { get; init; }
    public required string SourceRepoOwner { get; init; }
    public required string SourceRepoName { get; init; }
    public required bool HasRuntimeSpec { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsDefault { get; init; }
    public required int SortOrder { get; init; }
    public required bool IsArchived { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
