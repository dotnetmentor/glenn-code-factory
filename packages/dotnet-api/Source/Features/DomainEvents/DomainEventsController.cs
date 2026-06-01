using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.DomainEvents.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.DomainEvents;

[Route("api/domain-events")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[EnableRateLimiting("GeneralPolicy")]
public class DomainEventsController : BaseApiController
{
    public DomainEventsController(IMediator mediator, ILogger<DomainEventsController> logger)
        : base(mediator, logger)
    {
    }

    [HttpGet]
    public async Task<ActionResult<GetAllDomainEventsResponse>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        [FromQuery] string? eventType = null,
        [FromQuery] string? entityType = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 100) pageSize = 100;

        var query = new GetAllDomainEventsQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            EventType = eventType,
            EntityType = entityType
        };

        var result = await Mediator.Send(query);

        return HandleResult(result);
    }
}
