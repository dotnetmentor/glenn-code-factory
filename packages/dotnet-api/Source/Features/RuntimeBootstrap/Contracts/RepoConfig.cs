using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// Git repository to clone during the <c>CloningRepo</c> stage. Optional on the
/// payload — the empty-spec / AI-curated onboarding path leaves <c>Repo</c> null
/// (Scene 5 of the runtime-bootstrap spec) so the daemon skips clone entirely.
///
/// <para><see cref="Url"/> is the HTTPS clone URL (e.g.
/// <c>https://github.com/owner/repo.git</c>). The daemon authenticates the
/// clone with a fresh, repo-scoped GitHub-App installation token fetched on
/// demand via the <c>GetRepoAccessToken</c> hub method — there is no
/// long-lived credential on this payload.</para>
///
/// <para><see cref="DeployKey"/> is the legacy SSH path: a PEM-formatted
/// private key the daemon would write transiently to a file referenced via
/// <c>GIT_SSH_COMMAND</c>, then delete after the clone. It is now optional and
/// will be null for any project provisioned through the GitHub-App flow. Kept
/// on the contract for backward compatibility with daemons that still know
/// only how to consume an SSH deploy key.</para>
/// </summary>
[TranspilationSource]
public record RepoConfig(string Url, string Branch, string? DeployKey);
