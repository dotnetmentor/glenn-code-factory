using Source.Features.DaemonVersions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.GetActiveDaemonVersion;

/// <summary>
/// Active daemon row for a channel — metadata only (no storage URL resolution).
/// </summary>
public sealed record GetActiveDaemonVersionQuery(string Channel = "stable")
    : IQuery<Result<DaemonVersion?>>;
