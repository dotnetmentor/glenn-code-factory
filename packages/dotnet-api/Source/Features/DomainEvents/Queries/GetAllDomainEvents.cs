using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DomainEvents.Queries;

public record GetAllDomainEventsQuery : IQuery<Result<GetAllDomainEventsResponse>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? EventType { get; init; }
    public string? EntityType { get; init; }
}

public record GetAllDomainEventsResponse
{
    public required List<DomainEventListItem> Events { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalPages { get; init; }
}

public record DomainEventListItem
{
    public required Guid Id { get; init; }
    public required string EventType { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public string? UserId { get; init; }
    public required string Payload { get; init; }
    public required DateTime OccurredAt { get; init; }
}

public class GetAllDomainEventsHandler : IQueryHandler<GetAllDomainEventsQuery, Result<GetAllDomainEventsResponse>>
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<GetAllDomainEventsHandler> _logger;

    public GetAllDomainEventsHandler(ApplicationDbContext dbContext, ILogger<GetAllDomainEventsHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<GetAllDomainEventsResponse>> Handle(GetAllDomainEventsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.StoredDomainEvents.AsQueryable();

        // Apply exact filters
        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            query = query.Where(e => e.EventType == request.EventType);
        }

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            query = query.Where(e => e.EntityType == request.EntityType);
        }

        // Apply search filter (contains on EventType, EntityType, EntityId)
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchTerm = request.Search.ToLower();
            query = query.Where(e =>
                e.EventType.ToLower().Contains(searchTerm) ||
                (e.EntityType != null && e.EntityType.ToLower().Contains(searchTerm)) ||
                (e.EntityId != null && e.EntityId.ToLower().Contains(searchTerm))
            );
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        // Order by OccurredAt descending (newest first), then paginate
        var events = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new DomainEventListItem
            {
                Id = e.Id,
                EventType = e.EventType,
                EntityType = e.EntityType ?? string.Empty,
                EntityId = e.EntityId ?? string.Empty,
                UserId = e.UserId,
                Payload = e.Payload,
                OccurredAt = e.OccurredAt
            })
            .ToListAsync(cancellationToken);

        var response = new GetAllDomainEventsResponse
        {
            Events = events,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages
        };

        return Result.Success(response);
    }
}
