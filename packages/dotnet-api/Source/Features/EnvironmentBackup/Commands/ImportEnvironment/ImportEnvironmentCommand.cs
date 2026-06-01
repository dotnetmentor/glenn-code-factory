using Source.Features.EnvironmentBackup.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.EnvironmentBackup.Commands.ImportEnvironment;

/// <summary>
/// Restore an environment from a versioned <see cref="EnvironmentSnapshotDto"/>. The whole
/// restore runs in a single DB transaction; secrets are re-encrypted under THIS
/// environment's keys. Idempotent upsert keyed on stable Ids — safe to re-run. SuperAdmin
/// only at the controller.
/// </summary>
public sealed record ImportEnvironmentCommand(EnvironmentSnapshotDto Snapshot)
    : ICommand<Result<EnvironmentImportSummary>>;
