using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.CursorModels.Models;
using Source.Features.CursorModels.Queries.ListActiveCursorModels;
using Source.Shared.Controllers;

namespace Source.Features.CursorModels.Controllers;

/// <summary>
/// Read surface for the <see cref="CursorModel"/> catalog — the set of
/// Cursor SDK model slugs the platform exposes through the chat surface when
/// a project's <c>AgentBackend</c> is <c>"cursor"</c>.
///
/// <para>Thin-slice scope: only the user-facing <c>GET active</c> endpoint
/// lands in this card. Admin CRUD (list-all, get-by-id, create, update,
/// delete) mirrors the AgentModels / OpencodeModels surface and ships in a
/// later slice — the seeded rows are sufficient for dogfood.</para>
///
/// <para><b>Return types.</b> The action declares an explicit
/// <c>ActionResult&lt;List&lt;CursorModelDto&gt;&gt;</c> with a matching
/// <see cref="ProducesResponseTypeAttribute"/> so Swagger / Orval emit a
/// concrete TypeScript type for the frontend's <c>useGetApiCursorModelsActive</c>
/// React-Query hook.</para>
/// </summary>
[ApiController]
[Route("api/cursor-models")]
[Authorize]
[Tags("CursorModels")]
public sealed class CursorModelsController : BaseApiController
{
    public CursorModelsController(IMediator mediator, ILogger<CursorModelsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// List every <em>active</em> catalog row. Open to any authenticated user —
    /// drives the Cursor model picker on the project settings page when the
    /// project's agent backend is set to <c>"cursor"</c>.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(List<CursorModelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<CursorModelDto>>> ListActive(CancellationToken ct)
    {
        var result = await Mediator.Send(new ListActiveCursorModelsQuery(), ct);
        return Ok(result.Value);
    }
}
