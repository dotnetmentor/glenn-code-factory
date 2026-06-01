using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Mcp.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Mcp.Controllers;

/// <summary>
/// Daemon-facing cold-boot delivery for MCP server registrations. A freshly
/// respawned daemon calls <c>GET /api/runtimes/{runtimeId}/bootstrap-mcp-config</c>
/// on boot and receives the list of enabled MCP servers (name + version +
/// base URL) it should wire into the Cursor SDK before accepting any agent
/// turns.
///
/// <para><b>Why HTTP and not SignalR.</b> Same reasoning as
/// <see cref="Source.Features.ProjectSecrets.Controllers.BootstrapEnvController"/>:
/// bootstrap runs before the SignalR connection is established — the daemon
/// needs the MCP config materialised in-process before its hub client even
/// starts negotiating. HTTP keeps the daemon-side bootstrap stage trivial: a
/// single fetch with the runtime token in the <c>Authorization: Bearer</c>
/// header.</para>
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>
/// and <see cref="Source.Features.ProjectSecrets.Controllers.BootstrapEnvController"/>:
/// this is a thin passthrough — one runtime existence read + one filtered
/// catalog read. Wrapping in a query handler would add files without changing
/// behaviour. The slice stays thin and the controller talks straight to the
/// DbContext.</para>
///
/// <para><b>Why no audit.</b> Bootstrap-mcp-config is high-volume (one call per
/// daemon boot, and daemons restart on every image redeploy + every idle-timeout
/// respawn) and the payload contains no secrets — only public MCP catalog
/// entries already discoverable to anyone with a runtime token. An audit row
/// per call would balloon the audit table without recording anything not
/// already obvious from the daemon's lifecycle events. Contrast with
/// <see cref="Source.Features.ProjectSecrets.Controllers.BootstrapEnvController"/>,
/// which DOES audit because it delivers decrypted secrets.</para>
///
/// <para><b>Auth.</b> Gated on <c>[Authorize(AuthenticationSchemes = "RuntimeToken")]</c>
/// — the <see cref="RuntimeTokenAuthenticationDefaults.SchemeName"/> scheme
/// registered in <see cref="AuthenticationExtensions.AddRuntimeTokenAuthScheme"/>.
/// Signature, lifetime, issuer, audience, and revocation are all verified by
/// the JWT bearer middleware before the action runs (missing/invalid/expired/
/// revoked → 401 at the middleware layer); we only need to enforce that the
/// token's runtime id matches the path. Mismatched claim → 403, not 401, since
/// the caller IS authenticated, just unauthorised for this resource. Same
/// pattern as <c>RuntimeStatusController.GetActiveSession</c>.</para>
///
/// <para><b>Per-project enablement deferred.</b> For now the single switch is
/// the catalog-level <see cref="McpServer.DefaultEnabled"/> flag. Per-project
/// override rows live in a future table that ships once we have multiple MCPs
/// to toggle; until then every project receives the same enabled set.</para>
/// </summary>
[ApiController]
[Route("api/runtimes/{runtimeId:guid}/bootstrap-mcp-config")]
[Tags("BootstrapMcp")]
public class BootstrapMcpConfigController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public BootstrapMcpConfigController(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Return every catalog-enabled MCP server with a daemon-resolvable base
    /// URL. Empty list is still 200 (not 204) — clearer for the daemon, which
    /// treats "200 with []" as "no MCPs to wire". 401 is handled by the JWT
    /// bearer middleware (token missing/invalid/expired/revoked); 403 is
    /// returned when the principal IS authenticated but the <c>rt_runtime</c>
    /// claim doesn't match the path. 404 is returned when the runtime row is
    /// gone (including soft-deleted, via the global query filter).
    ///
    /// <para>The response's <see cref="McpEntry.BaseUrl"/> is composed at
    /// request time from <c>HttpContext.Request.Scheme</c> +
    /// <c>HttpContext.Request.Host</c> + <c>/api/mcp/{name}/{version}</c>. We
    /// don't hardcode <c>https://</c> or a hostname because Fly + cloudflare
    /// tunnel rewrites both, and pinning either would break the tunnel-routed
    /// dev surface. <see cref="McpServer"/> deliberately does not store BaseUrl
    /// for the same reason. The <c>/api/</c> prefix is mandatory — the
    /// Cloudflare tunnel only forwards <c>/api/*</c> to upstream Kestrel; a
    /// bare <c>/mcp/*</c> URL 404s at the edge before reaching the backend
    /// (see <see cref="ProjectKanban.Mcp.KanbanMcpController"/> route doc for
    /// spec pointer).</para>
    /// </summary>
    [HttpGet]
    [Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
    [ProducesResponseType(typeof(BootstrapMcpConfigResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BootstrapMcpConfigResponse>> Get(
        Guid runtimeId,
        CancellationToken ct)
    {
        // The RuntimeToken JWT scheme has already validated signature + lifetime
        // + issuer/audience and consulted the revocation cache (a missing/invalid
        // token is rejected at the middleware layer with 401). We still enforce
        // that the token's runtimeId claim matches the path — a daemon may only
        // bootstrap itself. Mismatched claim → 403, not 401, since the caller
        // *is* authenticated, just unauthorised for this resource.
        var claimRuntimeIdRaw = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRuntimeIdRaw, out var claimRuntimeId) || claimRuntimeId != runtimeId)
        {
            return Forbid();
        }

        // Default query — soft-deleted runtimes are filtered out by the global
        // filter, which is exactly what we want here: a torn-down runtime has
        // no bootstrap bundle. Mirrors RuntimeStatusController.GetActiveSession
        // and BootstrapEnvController.GetBootstrapEnv.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // Catalog-level enabled set — sorted by Name for deterministic response
        // bodies (helpful for diff-friendly daemon-side logs). Per-project
        // override is deferred per Card 4 spec; today every runtime receives
        // the same enabled MCPs.
        var servers = await _db.McpServers
            .Where(s => s.DefaultEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        // BaseUrl is composed from the inbound request — see the action's XML
        // doc for why we don't hardcode scheme/host.
        var scheme = Request.Scheme;
        var host = Request.Host.Value;
        var entries = servers
            .Select(s => new McpEntry(s.Name, s.Version, $"{scheme}://{host}/api/mcp/{s.Name}/{s.Version}"))
            .ToList();

        return Ok(new BootstrapMcpConfigResponse(entries));
    }
}
