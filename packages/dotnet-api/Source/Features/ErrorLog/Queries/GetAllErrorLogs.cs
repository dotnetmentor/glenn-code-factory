using Microsoft.EntityFrameworkCore;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Queries;

public record GetAllErrorLogsQuery : IQuery<Result<GetAllErrorLogsResponse>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Search { get; init; }
    public string? Source { get; init; }
    public string? Severity { get; init; }
    public bool? IsResolved { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
}

public record ErrorLogListItem
{
    public required Guid Id { get; init; }
    public required string Message { get; init; }
    public required string Source { get; init; }
    public required string Severity { get; init; }
    public required bool? IsResolved { get; init; }
    public required string? CorrelationId { get; init; }
    public required string? RequestPath { get; init; }
    public required DateTime? ResolvedAt { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record GetAllErrorLogsResponse
{
    public required List<ErrorLogListItem> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalPages { get; init; }
}

public class GetAllErrorLogsQueryHandler : IQueryHandler<GetAllErrorLogsQuery, Result<GetAllErrorLogsResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<GetAllErrorLogsQueryHandler> _logger;

    public GetAllErrorLogsQueryHandler(ApplicationDbContext context, ILogger<GetAllErrorLogsQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<GetAllErrorLogsResponse>> Handle(GetAllErrorLogsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.ErrorLogs.AsQueryable();

        // Filter by source
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            query = query.Where(x => x.Source == request.Source);
        }

        // Filter by severity
        if (!string.IsNullOrWhiteSpace(request.Severity))
        {
            query = query.Where(x => x.Severity == request.Severity);
        }

        // Filter by resolved status
        if (request.IsResolved.HasValue)
        {
            query = query.Where(x => x.IsResolved == request.IsResolved.Value);
        }

        // Filter by date range
        if (request.FromDate.HasValue)
        {
            query = query.Where(x => x.CreatedAt >= request.FromDate.Value);
        }

        if (request.ToDate.HasValue)
        {
            query = query.Where(x => x.CreatedAt <= request.ToDate.Value);
        }

        // Search on message
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchTerm = request.Search.ToLower();
            query = query.Where(x => x.Message.ToLower().Contains(searchTerm));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new ErrorLogListItem
            {
                Id = x.Id,
                Message = x.Message,
                Source = x.Source,
                Severity = x.Severity,
                IsResolved = x.IsResolved,
                CorrelationId = x.CorrelationId,
                RequestPath = x.RequestPath,
                ResolvedAt = x.ResolvedAt,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        var response = new GetAllErrorLogsResponse
        {
            Items = items,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = totalPages
        };

        return Result.Success(response);
    }
}
