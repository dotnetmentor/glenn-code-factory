using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Mints the signed CSRF state token + builds the GitHub install URL the controller redirects to.
/// The controller is responsible for setting the <c>gh_install_state</c> cookie + Location header.
/// </summary>
public record StartGithubInstallCommand : ICommand<Result<StartGithubInstallResponse>>;

public sealed record StartGithubInstallResponse
{
    public required string RedirectUrl { get; init; }
    public required string StateToken { get; init; }
    public required TimeSpan StateTtl { get; init; }
}

public sealed class StartGithubInstallHandler : ICommandHandler<StartGithubInstallCommand, Result<StartGithubInstallResponse>>
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly IWorkspaceContext _ctx;
    private readonly IGithubInstallStateService _state;
    private readonly IGithubOptionsAccessor _options;

    public StartGithubInstallHandler(
        IWorkspaceContext ctx,
        IGithubInstallStateService state,
        IGithubOptionsAccessor options)
    {
        _ctx = ctx;
        _state = state;
        _options = options;
    }

    public Task<Result<StartGithubInstallResponse>> Handle(StartGithubInstallCommand request, CancellationToken cancellationToken)
    {
        var options = _options.Current;
        if (string.IsNullOrWhiteSpace(options.AppSlug))
        {
            return Task.FromResult(Result.Failure<StartGithubInstallResponse>("GitHub App is not configured"));
        }

        var token = _state.Issue(_ctx.Id, StateTtl);
        var redirect = $"https://github.com/apps/{Uri.EscapeDataString(options.AppSlug)}/installations/new?state={Uri.EscapeDataString(token)}";

        return Task.FromResult(Result.Success(new StartGithubInstallResponse
        {
            RedirectUrl = redirect,
            StateToken = token,
            StateTtl = StateTtl,
        }));
    }
}
