using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Queries.ListInstallationRepos;
using Source.Features.GitHub.Queries.ListRepoBranches;
using Source.Shared.Controllers;
using Source.Shared.Results;

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Installation-scoped, JWT-authenticated GitHub read endpoints used by the smoke-test
/// onboarding flow's repo + branch pickers. Sits at <c>/api/github</c> alongside
/// <see cref="GithubAuthController"/> and <see cref="GithubInstallCallbackController"/> — they
/// share the prefix but never collide on action paths.
///
/// <para>These endpoints are deliberately distinct from <see cref="GithubInstallController"/>'s
/// workspace-scoped reads (<c>/api/workspaces/{slug}/github/...</c>): the install controller
/// returns the rows we already store locally, while this controller calls GitHub LIVE on every
/// request — per spec, no caching. The frontend uses these for the live-data pickers; the
/// install controller's endpoints feed admin/management UIs.</para>
///
/// <para>Both endpoints gate on <c>WorkspaceMembership</c>: the caller must be a member of the
/// installation's workspace. A miss — installation does not exist OR caller is not a member —
/// returns 404, never 403, so an attacker can't probe for installation existence.</para>
/// </summary>
[ApiController]
[Route("api/github")]
[Authorize]
[Tags("GitHub")]
public class GithubController : BaseApiController
{
    public GithubController(IMediator mediator, ILogger<GithubController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Live list of repositories accessible through a GitHub App installation. Calls GitHub
    /// <c>GET /installation/repositories</c> (paginated, capped at the first 100 entries — see
    /// <see cref="Services.IGithubApiClient.ListInstallationRepositoriesAsync"/>).
    ///
    /// <para>When the optional <paramref name="workspaceId"/> query param is supplied, each
    /// returned row is cross-referenced against that workspace's live projects on this
    /// installation and the <see cref="GithubRepoListItemDto.LinkedProjectId"/> /
    /// <see cref="GithubRepoListItemDto.LinkedProjectName"/> fields are populated when a
    /// matching project exists. This drives the "Open existing project" affordance on the
    /// New Session repo picker without a separate round-trip. When the param is omitted
    /// (existing callers) both fields are left <c>null</c> — additive and backward-
    /// compatible.</para>
    /// </summary>
    /// <param name="installationId">Local DB Guid of the <c>GithubInstallation</c> row, NOT the GitHub-side numeric id.</param>
    /// <param name="workspaceId">Optional workspace context for the "linked project" cross-reference.</param>
    /// <param name="ct">Cancellation token (forwarded to the GitHub HTTP call).</param>
    [HttpGet("installations/{installationId:guid}/repos")]
    [ProducesResponseType(typeof(List<GithubRepoListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<GithubRepoListItemDto>>> ListRepos(
        Guid installationId,
        [FromQuery] Guid? workspaceId,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new ListInstallationReposQuery(installationId, userId, workspaceId), ct);
        return MapResult(result);
    }

    /// <summary>
    /// Live list of branches for a single repo accessible through the installation. Issues two
    /// GitHub calls — <c>GET /repos/{owner}/{repo}</c> (to discover the default branch) and
    /// <c>GET /repos/{owner}/{repo}/branches</c> (the list itself, capped at the first 100).
    /// </summary>
    [HttpGet("installations/{installationId:guid}/repos/{owner}/{repo}/branches")]
    [ProducesResponseType(typeof(List<GithubBranchListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<GithubBranchListItemDto>>> ListBranches(
        Guid installationId,
        string owner,
        string repo,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new ListRepoBranchesQuery(installationId, owner, repo, userId), ct);
        return MapResult(result);
    }

    // -------------------------------------------------------------------------------
    // Result → ActionResult mapping. Centralised here so both endpoints share identical
    // 404-vs-400 logic via the NotFoundPrefix sentinel set on the handler side.
    // -------------------------------------------------------------------------------

    private ActionResult<T> MapResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        if (result.Error?.StartsWith(ListInstallationReposHandler.NotFoundPrefix, StringComparison.Ordinal) == true ||
            result.Error?.StartsWith(ListRepoBranchesHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
        {
            // Don't echo the detail message — the whole point of the sentinel is to NOT leak
            // whether the row exists or the caller just isn't allowed to see it.
            Logger.LogWarning("GitHub installation lookup denied: {Error}", result.Error);
            return NotFound();
        }

        Logger.LogWarning("GitHub request failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }
}
