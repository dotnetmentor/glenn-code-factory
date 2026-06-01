using MediatR;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Mcp.Framework;
using Source.Features.Specifications.Commands;
using Source.Features.Specifications.Queries;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Source.Features.Specifications.Mcp;

/// <summary>
/// MCP server exposing the project's specifications — the surface the
/// <c>@planning</c> subagent and the main agent both use to draft, read, and
/// remove specs (platform-planning-kanban spec). Mirrors the
/// <see cref="Source.Features.ProjectKanban.Mcp.KanbanMcpController"/> shape:
/// every action force-scopes to <c>this.ProjectId</c> (claims-derived, never
/// trusted from the request body) and records
/// <c>"runtime:&lt;runtimeId&gt;"</c> as the actor.
///
/// <para><b>Security boundary.</b> The framework's forbidden-field strip
/// (<see cref="McpControllerBase.InvokeAsync{TIn,TOut}"/>) zeroes any
/// client-supplied <c>projectId</c> / <c>tenantId</c> / <c>runtimeId</c> on
/// the input record. Handler-side filters on <c>ProjectId</c> are the second
/// line of defence — even a reflection-evading payload can't widen the query
/// past the runtime's project.</para>
///
/// <para><b>Routing.</b> Sits under <c>api/mcp/specifications/v1</c> — note
/// the <c>api/</c> prefix is required so the route rides the same Cloudflare
/// tunnel ingress rule as every other backend endpoint (the tunnel forwards
/// only <c>/api/*</c> to upstream; bare <c>/mcp/*</c> 404s at the edge before
/// it ever reaches Kestrel — see <see cref="ProjectKanban.Mcp.KanbanMcpController"/>
/// for the regression context). The version segment matches the
/// <see cref="McpServerAttribute"/> version so a future v2 can ship
/// side-by-side without breaking the daemon.</para>
/// </summary>
[ApiController]
[Route("api/mcp/specifications/v1")]
[McpServer(name: "specifications", version: "v1")]
[Tags("SpecificationsMcp")]
public class SpecificationsMcpController : McpControllerBase
{
    private readonly IMediator _mediator;

    public SpecificationsMcpController(
        IMediator mediator,
        ApplicationDbContext db,
        ILogger<McpControllerBase> logger,
        McpRateLimiter rateLimiter)
        : base(db, logger, rateLimiter)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Idempotent upsert: create the spec if its slug is new, update name +
    /// content if a non-deleted row already exists for that
    /// <c>(ProjectId, Slug)</c>. The response carries a <c>Created</c> flag so
    /// the caller can log "drafted N, revised M" totals without an extra read.
    /// </summary>
    [HttpPost("saveSpecification")]
    [ProducesResponseType(typeof(McpResponse<SaveSpecificationResponse>), 200)]
    public Task<IActionResult> SaveSpecification(
        [FromBody] SaveSpecificationInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "saveSpecification",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<SaveSpecificationResponse>("invalid_input");
                }
                return await _mediator.Send(
                    new SaveSpecificationCommand(
                        ProjectId,
                        i.Slug,
                        i.Name,
                        i.Content,
                        // CreatedBy must be null on the MCP path — the column
                        // FKs into AspNetUsers and the daemon's runtime token
                        // has no Identity user behind it. The runtime actor is
                        // captured in the McpCall audit row by the framework
                        // (see SaveSpecificationCommand doc comment).
                        CreatedBy: null),
                    ct);
            },
            ct: ct);

    /// <summary>
    /// Fetch the full spec (including markdown body) by slug. Returns
    /// <c>not_found</c> when the slug is unknown or belongs to another
    /// project — uniform 404 stance to avoid cross-tenant existence leaks.
    /// </summary>
    [HttpPost("readSpecification")]
    [ProducesResponseType(typeof(McpResponse<SpecificationDto>), 200)]
    public Task<IActionResult> ReadSpecification(
        [FromBody] ReadSpecificationInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "readSpecification",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<SpecificationDto>("invalid_input");
                }
                return await _mediator.Send(
                    new ReadSpecificationQuery(ProjectId, i.Slug), ct);
            },
            ct: ct);

    /// <summary>
    /// List every non-deleted spec for the runtime's project, ordered by most
    /// recently updated. <c>Content</c> is omitted from the summary; clients
    /// drill into <see cref="ReadSpecification"/> when they need the body.
    /// </summary>
    [HttpPost("listSpecifications")]
    [ProducesResponseType(typeof(McpResponse<List<SpecificationSummaryDto>>), 200)]
    public Task<IActionResult> ListSpecifications(
        CancellationToken ct) =>
        InvokeAsync<object?, List<SpecificationSummaryDto>>(
            method: "listSpecifications",
            input: null,
            handler: async _ => await _mediator.Send(
                new ListSpecificationsQuery(ProjectId), ct),
            ct: ct);

    /// <summary>
    /// Soft-delete a spec by slug. Returns <c>not_found</c> when the slug is
    /// unknown or already deleted; the slug becomes available for a fresh
    /// <see cref="SaveSpecification"/> immediately afterwards because the
    /// unique index is filtered to <c>IsDeleted = false</c>.
    /// </summary>
    [HttpPost("deleteSpecification")]
    [ProducesResponseType(typeof(McpResponse<Unit>), 200)]
    public Task<IActionResult> DeleteSpecification(
        [FromBody] DeleteSpecificationInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "deleteSpecification",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<Unit>("invalid_input");
                }
                var result = await _mediator.Send(
                    new DeleteSpecificationCommand(ProjectId, i.Slug), ct);
                return result.IsSuccess
                    ? Result.Success(Unit.Value)
                    : Result.Failure<Unit>(result.Error!);
            },
            ct: ct);

}
