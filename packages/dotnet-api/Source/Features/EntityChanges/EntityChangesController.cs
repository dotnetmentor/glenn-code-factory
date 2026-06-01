using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.EntityChanges.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.EntityChanges;

[Route("api/entity-changes")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[EnableRateLimiting("GeneralPolicy")]
public class EntityChangesController : BaseApiController
{
    public EntityChangesController(IMediator mediator, ILogger<EntityChangesController> logger)
        : base(mediator, logger)
    {
    }

    [HttpGet]
    public async Task<ActionResult<GetAllEntityChangesResponse>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? operation = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 100) pageSize = 100;

        var query = new GetAllEntityChangesQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            EntityType = entityType,
            Operation = operation
        };

        var result = await Mediator.Send(query);

        return HandleResult(result);
    }
}
