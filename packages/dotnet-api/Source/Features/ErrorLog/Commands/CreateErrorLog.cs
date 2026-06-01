using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Commands;

public record CreateErrorLogCommand : ICommand<Result<CreateErrorLogResponse>>
{
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestMethod { get; init; }
    public string? ContextData { get; init; }
    public bool? IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

public record CreateErrorLogResponse
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
}

public class CreateErrorLogCommandHandler : ICommandHandler<CreateErrorLogCommand, Result<CreateErrorLogResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CreateErrorLogCommandHandler> _logger;

    public CreateErrorLogCommandHandler(ApplicationDbContext context, ILogger<CreateErrorLogCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<CreateErrorLogResponse>> Handle(CreateErrorLogCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return Result.Failure<CreateErrorLogResponse>("Message is required");
        if (string.IsNullOrWhiteSpace(request.Source))
            return Result.Failure<CreateErrorLogResponse>("Source is required");
        if (string.IsNullOrWhiteSpace(request.Severity))
            return Result.Failure<CreateErrorLogResponse>("Severity is required");

        var entity = new Models.ErrorLog
        {
            Id = Guid.NewGuid(),
            Message = request.Message.Trim(),
            StackTrace = request.StackTrace?.Trim(),
            Source = request.Source.Trim(),
            Severity = request.Severity.Trim(),
            CorrelationId = request.CorrelationId?.Trim(),
            RequestPath = request.RequestPath?.Trim(),
            RequestMethod = request.RequestMethod?.Trim(),
            ContextData = request.ContextData?.Trim(),
            IsResolved = request.IsResolved,
            ResolvedAt = request.ResolvedAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ErrorLogs.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("ErrorLog created: {Id}", entity.Id);

        var response = new CreateErrorLogResponse
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
            CreatedAt = entity.CreatedAt
        };

        return Result.Success(response);
    }
}

