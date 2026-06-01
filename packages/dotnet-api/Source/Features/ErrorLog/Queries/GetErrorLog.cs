using Microsoft.EntityFrameworkCore;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Queries;

public record GetErrorLogQuery(Guid Id) : IQuery<Result<ErrorLogResponse>>;

public record ErrorLogResponse
{
    public required Guid Id { get; init; }
    public required string Message { get; init; }
    public required string? StackTrace { get; init; }
    public required string Source { get; init; }
    public required string Severity { get; init; }
    public required string? CorrelationId { get; init; }
    public required string? RequestPath { get; init; }
    public required string? RequestMethod { get; init; }
    public required string? ContextData { get; init; }
    public required bool? IsResolved { get; init; }
    public required DateTime? ResolvedAt { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public class GetErrorLogQueryHandler : IQueryHandler<GetErrorLogQuery, Result<ErrorLogResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<GetErrorLogQueryHandler> _logger;

    public GetErrorLogQueryHandler(ApplicationDbContext context, ILogger<GetErrorLogQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<ErrorLogResponse>> Handle(GetErrorLogQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.ErrorLogs
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity == null)
        {
            return Result.Failure<ErrorLogResponse>("ErrorLog not found");
        }

        var response = new ErrorLogResponse
        {
            Id = entity.Id,
            Message = entity.Message,
            StackTrace = entity.StackTrace,
            Source = entity.Source,
            Severity = entity.Severity,
            CorrelationId = entity.CorrelationId,
            RequestPath = entity.RequestPath,
            RequestMethod = entity.RequestMethod,
            ContextData = entity.ContextData,
            IsResolved = entity.IsResolved,
            ResolvedAt = entity.ResolvedAt,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };

        return Result.Success(response);
    }
}

