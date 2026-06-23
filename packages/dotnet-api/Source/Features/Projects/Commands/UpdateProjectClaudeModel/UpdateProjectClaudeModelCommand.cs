using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateProjectClaudeModel;

/// <summary>
/// Set (or clear) a project's default <see cref="Models.Project.ClaudeModelId"/>.
/// Backs <c>PATCH /api/projects/{projectId}/claude-model</c>.
///
/// <para>Mirrors <c>UpdateProjectCursorModelCommand</c> exactly — same shape,
/// same validation, same response. <c>null</c> clears the project default and
/// lets the daemon's <c>ClaudeFactory</c> fall back to the <c>ClaudeModels</c>
/// system default. A non-null id is validated against the <c>ClaudeModels</c>
/// table for existence + <c>IsActive == true</c>.</para>
///
/// <para><b>Validation.</b> Handler returns:</para>
/// <list type="bullet">
///   <item><see cref="UpdateProjectClaudeModelHandler.NotFoundPrefix"/> + detail
///         — project missing / tombstoned. Controller maps to 404.</item>
///   <item><see cref="UpdateProjectClaudeModelHandler.InvalidModelError"/> —
///         model id doesn't exist or is inactive. Controller maps to 400.</item>
/// </list>
/// </summary>
public sealed record UpdateProjectClaudeModelCommand(
    Guid ProjectId,
    Guid? ModelId
) : ICommand<Result<ProjectDto>>;
