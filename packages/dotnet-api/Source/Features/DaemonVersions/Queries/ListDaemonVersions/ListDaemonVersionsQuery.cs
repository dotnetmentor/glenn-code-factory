using Source.Features.DaemonVersions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.ListDaemonVersions;

/// <summary>
/// List all daemon versions, latest first. Today returns every row in every
/// channel; once channels other than <c>stable</c> exist we'll add a filter
/// param. Powers the (future) admin UI for daemon-version management.
/// </summary>
public sealed record ListDaemonVersionsQuery() : IQuery<Result<List<DaemonVersionDto>>>;
