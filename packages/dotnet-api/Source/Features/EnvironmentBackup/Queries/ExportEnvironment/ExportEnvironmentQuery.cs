using Source.Features.EnvironmentBackup.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.EnvironmentBackup.Queries.ExportEnvironment;

/// <summary>
/// Assemble the full versioned <see cref="EnvironmentSnapshotDto"/> for disaster
/// recovery / cloning. Reads every in-scope entity and decrypts all secrets to clear
/// text inside the returned blob. SuperAdmin-only at the controller.
/// </summary>
public sealed record ExportEnvironmentQuery() : IQuery<Result<EnvironmentSnapshotDto>>;
