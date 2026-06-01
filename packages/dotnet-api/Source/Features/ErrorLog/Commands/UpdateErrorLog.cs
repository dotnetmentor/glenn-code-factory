using Microsoft.EntityFrameworkCore;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Commands;

public record UpdateErrorLogCommand : ICommand<Result<UpdateErrorLogResponse>>
{
    public Guid Id { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public string? Source { get; init; }
    public string? Severity { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestMethod { get; init; }
    public string? ContextData { get; init; }
    public bool? IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

public record UpdateErrorLogResponse
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
    public required DateTime UpdatedAt { get; init; }
}

public class UpdateErrorLogCommandHandler : ICommandHandler<UpdateErrorLogCommand, Result<UpdateErrorLogResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<UpdateErrorLogCommandHandler> _logger;

    public UpdateErrorLogCommandHandler(ApplicationDbContext context, ILogger<UpdateErrorLogCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<UpdateErrorLogResponse>> Handle(UpdateErrorLogCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.ErrorLogs
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity == null)
        {
            return Result.Failure<UpdateErrorLogResponse>("ErrorLog not found");
        }

        if (!string.IsNullOrWhiteSpace(request.Message))
            entity.Message = request.Message!.Trim();
        if (!string.IsNullOrWhiteSpace(request.StackTrace))
            entity.StackTrace = request.StackTrace!.Trim();
        if (!string.IsNullOrWhiteSpace(request.Source))
            entity.Source = request.Source!.Trim();
        if (!string.IsNullOrWhiteSpace(request.Severity))
            entity.Severity = request.Severity!.Trim();
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            entity.CorrelationId = request.CorrelationId!.Trim();
        if (!string.IsNullOrWhiteSpace(request.RequestPath))
            entity.RequestPath = request.RequestPath!.Trim();
        if (!string.IsNullOrWhiteSpace(request.RequestMethod))
            entity.RequestMethod = request.RequestMethod!.Trim();
        if (!string.IsNullOrWhiteSpace(request.ContextData))
            entity.ContextData = request.ContextData!.Trim();

        // Handle resolve toggling: when IsResolved changes, auto-set ResolvedAt
        if (request.IsResolved.HasValue)
        {
            var wasResolved = entity.IsResolved == true;
            var nowResolved = request.IsResolved.Value;

            entity.IsResolved = nowResolved;

            if (nowResolved && !wasResolved)
            {
                // Resolving: set ResolvedAt to now
                entity.ResolvedAt = DateTime.UtcNow;
            }
            else if (!nowResolved && wasResolved)
            {
                // Unresolving: clear ResolvedAt
                entity.ResolvedAt = null;
            }
        }

        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("ErrorLog updated: {Id}", entity.Id);

        var response = new UpdateErrorLogResponse
        {
            Id = entity.Id,
            Message = entity.Message!,
            StackTrace = entity.StackTrace,
            Source = entity.Source!,
            Severity = entity.Severity!,
            CorrelationId = entity.CorrelationId,
            RequestPath = entity.RequestPath,
            RequestMethod = entity.RequestMethod,
            ContextData = entity.ContextData,
            IsResolved = entity.IsResolved,
            ResolvedAt = entity.ResolvedAt,
            UpdatedAt = entity.UpdatedAt
        };

        return Result.Success(response);
    }
}
