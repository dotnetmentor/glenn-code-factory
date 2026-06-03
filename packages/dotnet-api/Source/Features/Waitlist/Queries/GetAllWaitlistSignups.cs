using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Waitlist.Queries;

/// <summary>
/// Paged list of waitlist signups for the super-admin view. Newest first.
/// </summary>
public record GetAllWaitlistSignupsQuery : IQuery<Result<GetAllWaitlistSignupsResponse>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string? Search { get; init; }
}

public record WaitlistSignupListItem
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string? Source { get; init; }
    public required string? Note { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record GetAllWaitlistSignupsResponse
{
    public required List<WaitlistSignupListItem> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalPages { get; init; }
}

public class GetAllWaitlistSignupsQueryHandler
    : IQueryHandler<GetAllWaitlistSignupsQuery, Result<GetAllWaitlistSignupsResponse>>
{
    private readonly ApplicationDbContext _context;

    public GetAllWaitlistSignupsQueryHandler(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<GetAllWaitlistSignupsResponse>> Handle(
        GetAllWaitlistSignupsQuery request,
        CancellationToken cancellationToken)
    {
        var query = _context.WaitlistSignups.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.ToLower();
            query = query.Where(x => x.Email.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new WaitlistSignupListItem
            {
                Id = x.Id,
                Email = x.Email,
                Source = x.Source,
                Note = x.Note,
                CreatedAt = x.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        return Result.Success(new GetAllWaitlistSignupsResponse
        {
            Items = items,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages,
        });
    }
}
