namespace Source.Features.SignalR.Services;

/// <summary>
/// Resolves the Cursor SDK API key for a project: project envelope → workspace envelope → host env var.
/// </summary>
public interface IAgentSecretsResolver
{
    Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct);
}

public sealed class AgentSecretsResolver : IAgentSecretsResolver
{
    private readonly ICursorApiKeyResolver _cursorKeys;

    public AgentSecretsResolver(ICursorApiKeyResolver cursorKeys)
    {
        _cursorKeys = cursorKeys;
    }

    public Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct) =>
        _cursorKeys.ResolveForProjectAsync(projectId, ct);
}
