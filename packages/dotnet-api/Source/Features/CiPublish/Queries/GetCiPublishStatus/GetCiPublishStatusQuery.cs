using Source.Features.CiPublish.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.CiPublish.Queries.GetCiPublishStatus;

public sealed record GetCiPublishStatusQuery(string? GitSha = null)
    : IQuery<Result<CiPublishStatusDto>>;
