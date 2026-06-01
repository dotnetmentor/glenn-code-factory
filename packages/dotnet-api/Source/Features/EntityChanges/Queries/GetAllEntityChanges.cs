using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.EntityChanges.Queries;

public record GetAllEntityChangesQuery : IQuery<Result<GetAllEntityChangesResponse>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? EntityType { get; init; }
    public string? Operation { get; init; }
}

public record GetAllEntityChangesResponse
{
    public required List<EntityChangeListItem> Changes { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalPages { get; init; }
}

public record EntityChangeListItem
{
    public required Guid Id { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Operation { get; init; }
    public required string ChangedProperties { get; init; }
    public string? UserId { get; init; }
    public required DateTime OccurredAt { get; init; }
}

public class GetAllEntityChangesHandler : IQueryHandler<GetAllEntityChangesQuery, Result<GetAllEntityChangesResponse>>
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<GetAllEntityChangesHandler> _logger;

    public GetAllEntityChangesHandler(ApplicationDbContext dbContext, ILogger<GetAllEntityChangesHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<GetAllEntityChangesResponse>> Handle(GetAllEntityChangesQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.StoredEntityChanges.AsQueryable();

        // Apply exact filters
        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            query = query.Where(e => e.EntityType == request.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(request.Operation))
        {
            query = query.Where(e => e.Operation == request.Operation);
        }

        // Apply search filter (contains on EntityType, EntityId)
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchTerm = request.Search.ToLower();
            query = query.Where(e =>
                e.EntityType.ToLower().Contains(searchTerm) ||
                e.EntityId.ToLower().Contains(searchTerm)
            );
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        // Order by OccurredAt descending (newest first), then paginate
        var changes = await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new EntityChangeListItem
            {
                Id = e.Id,
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                Operation = e.Operation,
                ChangedProperties = e.ChangedProperties,
                UserId = e.UserId,
                OccurredAt = e.OccurredAt
            })
            .ToListAsync(cancellationToken);

        var response = new GetAllEntityChangesResponse
        {
            Changes = changes,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages
        };

        return Result.Success(response);
    }
}
