using Source.Features.DaemonVersions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;

/// <summary>
/// Resolve a channel to its currently-active <see cref="DaemonVersionDto"/>.
/// Used by:
/// <list type="bullet">
///   <item>The runtime container's bootstrap script (cold-boot, no auth) —
///         must be on a public endpoint;</item>
///   <item><c>RuntimeProvisionerJob</c> when stamping <c>DAEMON_VERSION</c> +
///         <c>DAEMON_BUNDLE_URL</c> + <c>DAEMON_BUNDLE_SHA256</c> on a new
///         Fly machine's env.</item>
/// </list>
/// Failure ("no version published yet") maps to a 404 in the controller.
/// </summary>
public sealed record ResolveDaemonVersionQuery(
    string Channel = "stable"
) : IQuery<Result<DaemonVersionDto>>;
