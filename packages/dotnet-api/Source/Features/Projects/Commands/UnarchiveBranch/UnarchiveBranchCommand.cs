using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UnarchiveBranch;

/// <summary>
/// Restore a soft-archived <see cref="Models.ProjectBranch"/> back to the
/// active set. Reverse of <see cref="ArchiveBranch.ArchiveBranchCommand"/>.
/// Idempotent — unarchiving an active branch is a success no-op.
///
/// <para><b>Runtime side-effect: none.</b> A suspended runtime wakes on the
/// next user activity through the existing wake-on-connect path; eagerly
/// booting on unarchive would burn Fly resources for branches the user merely
/// "un-hid".</para>
/// </summary>
public sealed record UnarchiveBranchCommand(
    Guid ProjectId,
    Guid BranchId
) : ICommand<Result>;
