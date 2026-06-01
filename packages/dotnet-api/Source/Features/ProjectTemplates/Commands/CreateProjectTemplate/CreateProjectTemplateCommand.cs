using Source.Features.ProjectTemplates.Queries.GetProjectTemplate;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.CreateProjectTemplate;

/// <summary>
/// Create a new curated starter (<see cref="Models.ProjectTemplate"/>) in the
/// global catalogue. Super-admin only — the controller gate is the source of
/// truth; the handler re-checks via <c>UserManager</c> as defence in depth so
/// any future programmatic caller (jobs, internal endpoints) can't slip past
/// the role attribute.
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>not_authorized</c> — caller is missing the SuperAdmin role.</item>
///   <item><c>invalid_name</c> / <c>invalid_slug</c> / <c>invalid_description</c> /
///         <c>invalid_icon_key</c> / <c>invalid_source_repo_owner</c> /
///         <c>invalid_source_repo_name</c> — field shape / length violation.</item>
///   <item><c>spec_invalid</c> — non-null <c>RuntimeSpec</c> fails JSON parse
///         or structural validation against <c>RuntimeSpecV3</c>.</item>
///   <item><c>name_taken</c> — another non-tombstoned row already uses this name.</item>
///   <item><c>slug_taken</c> — another non-tombstoned row already uses this slug.</item>
/// </list>
/// </summary>
public sealed record CreateProjectTemplateCommand(
    string CallerUserId,
    string Name,
    string Slug,
    string? Description,
    string? IconKey,
    string SourceRepoOwner,
    string SourceRepoName,
    string? RuntimeSpec,
    bool IsActive,
    bool IsDefault,
    int SortOrder
) : ICommand<Result<ProjectTemplateDetail>>;
