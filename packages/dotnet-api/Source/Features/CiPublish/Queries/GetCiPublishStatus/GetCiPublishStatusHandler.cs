using MediatR;
using Source.Features.CiPublish.Models;
using Source.Features.DaemonVersions.Queries.GetActiveDaemonVersion;
using Source.Features.RuntimeImages.Queries.GetActiveRuntimeImage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.CiPublish.Queries.GetCiPublishStatus;

public sealed class GetCiPublishStatusHandler
    : IQueryHandler<GetCiPublishStatusQuery, Result<CiPublishStatusDto>>
{
    private readonly IMediator _mediator;

    public GetCiPublishStatusHandler(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task<Result<CiPublishStatusDto>> Handle(
        GetCiPublishStatusQuery request,
        CancellationToken cancellationToken)
    {
        string? daemonGitSha = null;
        var daemonResult = await _mediator.Send(new GetActiveDaemonVersionQuery("stable"), cancellationToken);
        if (daemonResult.IsSuccess && daemonResult.Value is not null)
        {
            daemonGitSha = daemonResult.Value.GitSha;
        }

        string? runtimeGitSha = null;
        var runtimeResult = await _mediator.Send(new GetActiveRuntimeImageQuery(), cancellationToken);
        if (runtimeResult.IsSuccess && runtimeResult.Value is not null)
        {
            runtimeGitSha = runtimeResult.Value.GitSha;
        }

        var requestedSha = string.IsNullOrWhiteSpace(request.GitSha) ? null : request.GitSha.Trim();

        return Result.Success(new CiPublishStatusDto
        {
            DaemonStableGitSha = daemonGitSha,
            RuntimeActiveGitSha = string.IsNullOrWhiteSpace(runtimeGitSha) ? null : runtimeGitSha,
            DaemonPublishedForRequestedSha = MatchesSha(requestedSha, daemonGitSha),
            RuntimePublishedForRequestedSha = MatchesSha(requestedSha, runtimeGitSha),
        });
    }

    private static bool MatchesSha(string? requested, string? published) =>
        !string.IsNullOrWhiteSpace(requested)
        && !string.IsNullOrWhiteSpace(published)
        && string.Equals(requested, published.Trim(), StringComparison.OrdinalIgnoreCase);
}
