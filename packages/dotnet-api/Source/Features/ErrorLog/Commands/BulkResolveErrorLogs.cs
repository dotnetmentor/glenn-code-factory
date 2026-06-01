using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Commands;

public record BulkResolveErrorLogsCommand : ICommand<Result<BulkResolveErrorLogsResponse>>
{
    public required List<Guid> Ids { get; init; }
    public required bool IsResolved { get; init; }
}

public record BulkResolveErrorLogsResponse
{
    public required int UpdatedCount { get; init; }
}

public class BulkResolveErrorLogsCommandHandler : ICommandHandler<BulkResolveErrorLogsCommand, Result<BulkResolveErrorLogsResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<BulkResolveErrorLogsCommandHandler> _logger;

    public BulkResolveErrorLogsCommandHandler(ApplicationDbContext context, ILogger<BulkResolveErrorLogsCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<BulkResolveErrorLogsResponse>> Handle(BulkResolveErrorLogsCommand request, CancellationToken cancellationToken)
    {
        if (request.Ids == null || request.Ids.Count == 0)
        {
            return Result.Failure<BulkResolveErrorLogsResponse>("At least one error log ID is required");
        }

        var entities = await _context.ErrorLogs
            .Where(x => request.Ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Result.Success(new BulkResolveErrorLogsResponse { UpdatedCount = 0 });
        }

        var now = DateTime.UtcNow;

        foreach (var entity in entities)
        {
            var wasResolved = entity.IsResolved == true;
            var nowResolved = request.IsResolved;

            entity.IsResolved = nowResolved;

            if (nowResolved && !wasResolved)
            {
                entity.ResolvedAt = now;
            }
            else if (!nowResolved && wasResolved)
            {
                entity.ResolvedAt = null;
            }

            entity.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Bulk resolved {Count} error logs (IsResolved={IsResolved})", entities.Count, request.IsResolved);

        return Result.Success(new BulkResolveErrorLogsResponse { UpdatedCount = entities.Count });
    }
}
