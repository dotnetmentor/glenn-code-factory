using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.CiPublish.Models;
using Source.Features.DaemonVersions.Queries.GetActiveDaemonVersion;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeImages.Queries.GetActiveRuntimeImage;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.CiPublish.Queries.GetCiPublishStatus;

public sealed class GetCiPublishStatusHandler
    : IQueryHandler<GetCiPublishStatusQuery, Result<CiPublishStatusDto>>
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public GetCiPublishStatusHandler(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
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

        var daemonAlreadyPublished = await DaemonGitShaExistsAsync(requestedSha, cancellationToken);
        var runtimeAlreadyPublished = await RuntimeGitShaExistsAsync(requestedSha, cancellationToken);

        return Result.Success(new CiPublishStatusDto
        {
            DaemonStableGitSha = daemonGitSha,
            RuntimeActiveGitSha = string.IsNullOrWhiteSpace(runtimeGitSha) ? null : runtimeGitSha,
            DaemonPublishedForRequestedSha = daemonAlreadyPublished,
            RuntimePublishedForRequestedSha = runtimeAlreadyPublished,
        });
    }

    /// <summary>
    /// True when any daemon version row (active or historical) was built from <paramref name="gitSha"/>.
    /// </summary>
    private async Task<bool> DaemonGitShaExistsAsync(string? gitSha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gitSha))
        {
            return false;
        }

        var normalized = gitSha.Trim().ToLowerInvariant();
        return await _db.DaemonVersions
            .AsNoTracking()
            .AnyAsync(
                v => v.GitSha != null && v.GitSha.ToLower() == normalized,
                cancellationToken);
    }

    /// <summary>
    /// True when a non-yanked runtime image row exists for <paramref name="gitSha"/>.
    /// </summary>
    private async Task<bool> RuntimeGitShaExistsAsync(string? gitSha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gitSha))
        {
            return false;
        }

        var normalized = gitSha.Trim().ToLowerInvariant();
        return await _db.RuntimeImages
            .AsNoTracking()
            .AnyAsync(
                i => i.GitSha != null
                     && i.GitSha.ToLower() == normalized
                     && i.Status != RuntimeImageStatus.Yanked,
                cancellationToken);
    }
}
