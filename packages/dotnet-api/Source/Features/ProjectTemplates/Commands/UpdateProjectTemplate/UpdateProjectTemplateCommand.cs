using Source.Features.ProjectTemplates.Queries.GetProjectTemplate;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.UpdateProjectTemplate;

/// <summary>
/// Replace a starter's mutable fields in place. There is no version history;
/// existing projects that were forked from this starter are unaffected because
/// the runtime spec was snapshot-copied at create time.
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>not_authorized</c> — caller is missing the SuperAdmin role.</item>
///   <item><c>template_not_found</c> — id is missing or tombstoned.</item>
///   <item><c>invalid_*</c> — field shape / length violation (same codes as create).</item>
///   <item><c>spec_invalid</c> — non-null <c>RuntimeSpec</c> fails JSON parse or
///         structural validation against <c>RuntimeSpecV3</c>.</item>
///   <item><c>name_taken</c> / <c>slug_taken</c> — another non-tombstoned row owns the value.</item>
/// </list>
/// </summary>
public sealed record UpdateProjectTemplateCommand(
    Guid TemplateId,
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
