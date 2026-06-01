using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Placeholder handler for GitHub <c>pull_request</c> webhook events.
/// Accepted + acked but not yet processed — see TODO comment for P3+.
/// </summary>
public record HandleGithubPullRequestEventCommand(string DeliveryId, string RawPayload) : ICommand<Result>;

public sealed class HandleGithubPullRequestEventHandler
    : ICommandHandler<HandleGithubPullRequestEventCommand, Result>
{
    private readonly ILogger<HandleGithubPullRequestEventHandler> _logger;

    public HandleGithubPullRequestEventHandler(ILogger<HandleGithubPullRequestEventHandler> logger)
    {
        _logger = logger;
    }

    public Task<Result> Handle(HandleGithubPullRequestEventCommand request, CancellationToken cancellationToken)
    {
        // TODO P3+ — handle push/PR events (PR state tracking, review automation, etc.).
        _logger.LogInformation(
            "Received GitHub pull_request webhook (delivery {DeliveryId}); deferring processing to P3+.",
            request.DeliveryId);
        return Task.FromResult(Result.Success());
    }
}
