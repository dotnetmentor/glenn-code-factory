using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

[TranspilationSource]
public record BootstrapPayloadV2(
    string Version,
    RuntimeSpecV2 RuntimeSpec,
    List<EnvVar> EnvVars,
    HooksConfig? Hooks,
    List<McpServer> Mcps,
    RepoConfig? Repo,
    string? ModelSlug = null);
