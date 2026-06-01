using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.ArchiveBranch;

/// <summary>
/// Soft-archive a <see cref="Models.ProjectBranch"/>. Archive is reversible —
/// the branch row stays in the database with <c>IsArchived=true</c> + an
/// <c>ArchivedAt</c> stamp, and is hidden from sidebars and branch pickers but
/// stays visible to history / past-conversation queries.
///
/// <para><b>Refusals.</b> The default branch cannot be archived
/// (<see cref="ArchiveBranchHandler.IsDefaultError"/>). A branch with a turn in
/// flight (<c>AgentSession</c> in <c>Pending</c>/<c>Running</c>) cannot be
/// archived either (<see cref="ArchiveBranchHandler.HasRunningSessionError"/>)
/// — stop the turn first.</para>
///
/// <para><b>Idempotent.</b> Archiving an already-archived branch is a success
/// no-op.</para>
///
/// <para><b>Runtime side-effect.</b> If the branch's active runtime is in a
/// running-ish state (<c>Online</c> / <c>Booting</c> / <c>Bootstrapping</c> /
/// <c>Waking</c>) it's transitioned to <c>Suspending</c> in the same
/// SaveChanges, so the reconciler picks up the Fly machine stop. Other states
/// (already Suspended, Failed, …) are left alone.</para>
/// </summary>
public sealed record ArchiveBranchCommand(
    Guid ProjectId,
    Guid BranchId
) : ICommand<Result>;
