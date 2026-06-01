using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.DaemonVersions.Commands.PublishDaemonVersion;
using Source.Features.DaemonVersions.Models;
using Source.Features.DaemonVersions.Queries.ListDaemonVersions;
using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.DaemonVersions.Controllers;

/// <summary>
/// Endpoints for the "daemon-as-downloadable" version registry. The runtime
/// container's bootstrap script calls <c>GET /api/daemon-versions/resolve</c>
/// on cold-boot to discover which tarball to pull; admin operators publish
/// new versions via <c>POST /api/daemon-versions</c>.
///
/// <para><b>Auth split.</b> The <c>resolve</c> endpoint is intentionally
/// <see cref="AllowAnonymousAttribute"/> — the daemon doesn't have a runtime
/// token until it's actually running, and cold-boot needs to happen before
/// that. The publish + list endpoints both require a logged-in caller.</para>
/// </summary>
[ApiController]
[Route("api/daemon-versions")]
[Tags("DaemonVersions")]
public class DaemonVersionsController : BaseApiController
{
    public DaemonVersionsController(IMediator mediator, ILogger<DaemonVersionsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Publish a new daemon bundle. Atomically deactivates the previous
    /// active version in the same channel. Returns the new version + a
    /// publicly-resolvable download URL.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [Consumes("multipart/form-data")]
    // Bumped to 500MB after daemon bundles grew to include @cursor/sdk +
    // node_modules peers (SignalR, sqlite3 native binding, etc.).
    [RequestSizeLimit(500L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 500L * 1024 * 1024)]
    [ProducesResponseType(typeof(PublishDaemonVersionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PublishDaemonVersionResponse>> Publish(
        IFormFile file,
        [FromForm] string? channel = null,
        [FromForm] string? notes = null,
        [FromForm] string? sha256 = null)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No bundle file uploaded" });
        }

        await using var stream = file.OpenReadStream();

        var command = new PublishDaemonVersionCommand(
            BundleStream: stream,
            ContentDisposition: file.FileName ?? "daemon.tar.gz",
            Channel: string.IsNullOrWhiteSpace(channel) ? "stable" : channel,
            Notes: notes,
            PreComputedSha256: sha256);

        var result = await Mediator.Send(command);

        if (!result.IsSuccess)
        {
            Logger.LogWarning("PublishDaemonVersion failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Resolve a channel (default "stable") to its currently-active daemon
    /// version. **Public — no auth required.** The runtime container's
    /// bootstrap script calls this on cold-boot before it has any token to
    /// authenticate with.
    /// </summary>
    [HttpGet("resolve")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DaemonVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DaemonVersionDto>> Resolve([FromQuery] string channel = "stable")
    {
        var result = await Mediator.Send(new ResolveDaemonVersionQuery(channel));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(
                    ResolveDaemonVersionHandler.NotFoundPrefix,
                    StringComparison.Ordinal) == true)
            {
                Logger.LogInformation("ResolveDaemonVersion: 404 for channel {Channel}", channel);
                return NotFound(new { error = result.Error });
            }

            Logger.LogWarning("ResolveDaemonVersion failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// List all daemon versions, latest first. Powers the (future) admin
    /// rollout UI.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(List<DaemonVersionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<DaemonVersionDto>>> List()
    {
        var result = await Mediator.Send(new ListDaemonVersionsQuery());

        if (!result.IsSuccess)
        {
            Logger.LogWarning("ListDaemonVersions failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }
}
