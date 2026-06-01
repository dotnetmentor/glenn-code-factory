using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Kicks off the slim OAuth-only re-authorize flow for an existing GitHub installation
/// that doesn't have a usable User Access Token (UAT) — either never captured (legacy
/// installs that pre-date the feature) or refresh-expired (6+ months idle).
///
/// <para>This does NOT re-install the App on github.com — only the user-OAuth side. The
/// caller is the workspace's frontend, redirecting the user to the URL we return.</para>
/// </summary>
public record StartGithubUserAuthCommand(Guid GithubInstallationId)
    : ICommand<Result<StartGithubUserAuthResponse>>;

public sealed record StartGithubUserAuthResponse
{
    /// <summary>URL the frontend redirects the browser to (<c>https://github.com/login/oauth/authorize?…</c>).</summary>
    public required string AuthorizeUrl { get; init; }
}

public sealed class StartGithubUserAuthHandler
    : ICommandHandler<StartGithubUserAuthCommand, Result<StartGithubUserAuthResponse>>
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;
    private readonly IGithubInstallStateService _state;
    private readonly IGithubOptionsAccessor _options;

    public StartGithubUserAuthHandler(
        ApplicationDbContext db,
        IWorkspaceContext ctx,
        IGithubInstallStateService state,
        IGithubOptionsAccessor options)
    {
        _db = db;
        _ctx = ctx;
        _state = state;
        _options = options;
    }

    public async Task<Result<StartGithubUserAuthResponse>> Handle(
        StartGithubUserAuthCommand request,
        CancellationToken cancellationToken)
    {
        var options = _options.Current;
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return Result.Failure<StartGithubUserAuthResponse>("GitHub App ClientId is not configured");
        }

        // Existence + tenancy check rolled into one query — the installation must
        // belong to the workspace the caller is acting under.
        var installation = await _db.GithubInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.Id == request.GithubInstallationId && i.WorkspaceId == _ctx.Id,
                cancellationToken);
        if (installation is null)
        {
            return Result.Failure<StartGithubUserAuthResponse>("GitHub installation not found");
        }

        var token = _state.IssueReauth(_ctx.Id, installation.Id, StateTtl);

        // GitHub's OAuth authorize URL — same client id as the install-time flow but
        // without the App-install dance. The redirect_uri is set on the GitHub App,
        // so we don't pass it here (passing a mismatched value is rejected).
        var url = $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(options.ClientId)}&state={Uri.EscapeDataString(token)}";

        return Result.Success(new StartGithubUserAuthResponse { AuthorizeUrl = url });
    }
}
