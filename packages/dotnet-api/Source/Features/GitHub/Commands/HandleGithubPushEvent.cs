using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Placeholder handler for GitHub <c>push</c> webhook events.
/// We accept and acknowledge them so GitHub's delivery dashboard stays green
/// but don't yet act on the payload — that lives in P3+.
/// </summary>
public record HandleGithubPushEventCommand(string DeliveryId, string RawPayload) : ICommand<Result>;

public sealed class HandleGithubPushEventHandler
    : ICommandHandler<HandleGithubPushEventCommand, Result>
{
    private readonly ILogger<HandleGithubPushEventHandler> _logger;

    public HandleGithubPushEventHandler(ILogger<HandleGithubPushEventHandler> logger)
    {
        _logger = logger;
    }

    public Task<Result> Handle(HandleGithubPushEventCommand request, CancellationToken cancellationToken)
    {
        // TODO P3+ — handle push/PR events (commit ingestion, branch tracking, etc.).
        _logger.LogInformation(
            "Received GitHub push webhook (delivery {DeliveryId}); deferring processing to P3+.",
            request.DeliveryId);
        return Task.FromResult(Result.Success());
    }
}
