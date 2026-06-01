using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateProjectCursorModel;

/// <summary>
/// Set (or clear) a project's default <see cref="Models.Project.CursorModelId"/>.
/// Backs <c>PATCH /api/projects/{projectId}/cursor-model</c>.
///
/// <para>Mirrors <c>UpdateProjectOpencodeModelCommand</c> exactly — same shape,
/// same validation, same response. <c>null</c> clears the project default and
/// lets the daemon's <c>CursorFactory</c> fall back to its own default
/// (<c>"auto"</c>). A non-null id is validated against the <c>CursorModels</c>
/// table for existence + <c>IsActive == true</c>.</para>
///
/// <para><b>Validation.</b> Handler returns:</para>
/// <list type="bullet">
///   <item><see cref="UpdateProjectCursorModelHandler.NotFoundPrefix"/> + detail
///         — project missing / tombstoned. Controller maps to 404.</item>
///   <item><see cref="UpdateProjectCursorModelHandler.InvalidModelError"/> —
///         model id doesn't exist or is inactive. Controller maps to 400.</item>
/// </list>
/// </summary>
public sealed record UpdateProjectCursorModelCommand(
    Guid ProjectId,
    Guid? ModelId
) : ICommand<Result<ProjectDto>>;
