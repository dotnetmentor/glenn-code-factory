using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// Wire-shape payload broadcast to the <c>project-{ProjectId}</c> SignalR group
/// every time a <see cref="RuntimeProposal"/> changes status — Approved /
/// Edited / Rejected by the user, or Applied / Failed by the daemon ack. Drives
/// the UI's confirmation-card lifecycle in the chat panel: pending card morphs
/// into a result row without a refetch.
///
/// <para><see cref="AppliedSpec"/> is the JSON body that landed on disk — set
/// on Approved (mirrors <c>ProposedSpec</c>), Edited (user-edited body), and
/// preserved through the Applied / Failed ack. Null on Rejected. The same
/// "flat string" rationale as <see cref="RuntimeProposalCreatedPayload.ProposedSpec"/>
/// applies — the React client parses it for rendering, we don't reshape it.</para>
/// </summary>
[TranspilationSource]
public record RuntimeProposalUpdatedPayload(
    Guid ProposalId,
    Guid ProjectId,
    Guid RuntimeId,
    RuntimeProposalStatus Status,
    string? AppliedSpec,
    string? ErrorMessage,
    DateTime? DecidedAt,
    string? DecidedBy);
