using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.ClaudeModels.Models;
using Source.Features.ClaudeModels.Queries.ListActiveClaudeModels;
using Source.Shared.Controllers;

namespace Source.Features.ClaudeModels.Controllers;

/// <summary>
/// Read surface for the <see cref="ClaudeModel"/> catalog — the set of Claude
/// Agent SDK model slugs the platform exposes through the chat surface when a
/// conversation's <c>AgentBackend</c> is <c>"claude"</c>.
///
/// <para>Thin-slice scope: only the user-facing <c>GET active</c> endpoint
/// lands in this card, mirroring <c>CursorModelsController</c>. Admin CRUD
/// (list-all, get-by-id, create, update, delete) ships in a later slice — the
/// seeded rows are sufficient for dogfood.</para>
///
/// <para><b>Return types.</b> The action declares an explicit
/// <c>ActionResult&lt;List&lt;ClaudeModelDto&gt;&gt;</c> with a matching
/// <see cref="ProducesResponseTypeAttribute"/> so Swagger / Orval emit a
/// concrete TypeScript type for the frontend's <c>useGetApiClaudeModelsActive</c>
/// React-Query hook.</para>
/// </summary>
[ApiController]
[Route("api/claude-models")]
[Authorize]
[Tags("ClaudeModels")]
public sealed class ClaudeModelsController : BaseApiController
{
    public ClaudeModelsController(IMediator mediator, ILogger<ClaudeModelsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// List every <em>active</em> catalog row. Open to any authenticated user —
    /// drives the Claude model picker in the composer when the conversation's
    /// agent backend is set to <c>"claude"</c>.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(List<ClaudeModelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ClaudeModelDto>>> ListActive(CancellationToken ct)
    {
        var result = await Mediator.Send(new ListActiveClaudeModelsQuery(), ct);
        return Ok(result.Value);
    }
}
