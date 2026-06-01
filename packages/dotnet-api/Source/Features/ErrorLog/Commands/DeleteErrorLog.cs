using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Commands;

public record DeleteErrorLogCommand(Guid Id) : ICommand<Result<DeleteErrorLogResponse>>;

public record DeleteErrorLogResponse
{
    public required Guid Id { get; init; }
}

public class DeleteErrorLogCommandHandler : ICommandHandler<DeleteErrorLogCommand, Result<DeleteErrorLogResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<DeleteErrorLogCommandHandler> _logger;

    public DeleteErrorLogCommandHandler(ApplicationDbContext context, ILogger<DeleteErrorLogCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<DeleteErrorLogResponse>> Handle(DeleteErrorLogCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.ErrorLogs
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity == null)
        {
            return Result.Failure<DeleteErrorLogResponse>("ErrorLog not found");
        }

        _context.ErrorLogs.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation("ErrorLog deleted: {Id}", entity.Id);

        var response = new DeleteErrorLogResponse
        {
            Id = request.Id
        };

        return Result.Success(response);
    }
}



