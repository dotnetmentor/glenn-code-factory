namespace Source.Features.SignalR.Services;

/// <summary>
/// Resolves the per-project coding-agent SDK credentials the daemon needs:
/// the Cursor SDK key (project envelope → workspace envelope → host env var)
/// and the Anthropic API key for the Claude backend (project envelope → host
/// env var). One facade so <c>RuntimeHub.GetSecrets</c> has a single dependency
/// to assemble the secrets DTO.
/// </summary>
public interface IAgentSecretsResolver
{
    Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct);

    Task<string?> ResolveAnthropicApiKeyAsync(Guid projectId, CancellationToken ct);
}

public sealed class AgentSecretsResolver : IAgentSecretsResolver
{
    private readonly ICursorApiKeyResolver _cursorKeys;
    private readonly IAnthropicApiKeyResolver _anthropicKeys;

    public AgentSecretsResolver(
        ICursorApiKeyResolver cursorKeys,
        IAnthropicApiKeyResolver anthropicKeys)
    {
        _cursorKeys = cursorKeys;
        _anthropicKeys = anthropicKeys;
    }

    public Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct) =>
        _cursorKeys.ResolveForProjectAsync(projectId, ct);

    public Task<string?> ResolveAnthropicApiKeyAsync(Guid projectId, CancellationToken ct) =>
        _anthropicKeys.ResolveForProjectAsync(projectId, ct);
}
