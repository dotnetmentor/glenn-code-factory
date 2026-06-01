using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Queries;

public record GetUnresolvedErrorCountQuery : IQuery<Result<GetUnresolvedErrorCountResponse>>;

public record GetUnresolvedErrorCountResponse
{
    public required int Count { get; init; }
}

public class GetUnresolvedErrorCountQueryHandler : IQueryHandler<GetUnresolvedErrorCountQuery, Result<GetUnresolvedErrorCountResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<GetUnresolvedErrorCountQueryHandler> _logger;

    public GetUnresolvedErrorCountQueryHandler(ApplicationDbContext context, ILogger<GetUnresolvedErrorCountQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<GetUnresolvedErrorCountResponse>> Handle(GetUnresolvedErrorCountQuery request, CancellationToken cancellationToken)
    {
        var count = await _context.ErrorLogs
            .CountAsync(x => x.IsResolved == false || x.IsResolved == null, cancellationToken);

        var response = new GetUnresolvedErrorCountResponse
        {
            Count = count
        };

        return Result.Success(response);
    }
}
