using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Services;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetMiseVersions;

/// <summary>
/// Handler for <see cref="GetMiseVersionsQuery"/>. Delegates to
/// <see cref="IMiseVersionLookup"/> and shapes the response. Unknown tool
/// names are NOT a failure — the lookup returns an empty list and the response
/// echoes the tool back so the UI can render "no versions found" without a
/// failed request shape.
/// </summary>
public sealed class GetMiseVersionsHandler
    : IQueryHandler<GetMiseVersionsQuery, Result<MiseVersionsResponse>>
{
    public const string InvalidToolError = "invalid_tool";

    private readonly IMiseVersionLookup _lookup;

    public GetMiseVersionsHandler(IMiseVersionLookup lookup)
    {
        _lookup = lookup;
    }

    public async Task<Result<MiseVersionsResponse>> Handle(
        GetMiseVersionsQuery request,
        CancellationToken cancellationToken)
    {
        var tool = (request.Tool ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(tool))
        {
            return Result.Failure<MiseVersionsResponse>(InvalidToolError);
        }

        var versions = await _lookup.GetVersionsAsync(tool, cancellationToken);
        return Result.Success(new MiseVersionsResponse(tool, versions));
    }
}
