using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// HTTP-response projection of a <see cref="RuntimeProposal"/>. Returned from
/// the user-facing decision controller after Approve / Edit / Reject so the
/// frontend can update the confirmation card optimistically without waiting
/// for the corresponding <see cref="RuntimeProposalUpdatedPayload"/>
/// broadcast — the SignalR push covers OTHER tabs / clients; the originating
/// request gets the post-decision row in its own response.
///
/// <para>Same audit-trail field set as the entity, minus the audit/soft-delete
/// columns and the navigation property. <see cref="ProposedSpec"/> /
/// <see cref="AppliedSpec"/> ride as flat JSON strings — same rationale as the
/// <c>Created</c> / <c>Updated</c> SignalR payloads.</para>
/// </summary>
[TranspilationSource]
public record RuntimeProposalDto(
    Guid Id,
    Guid ProjectId,
    Guid RuntimeId,
    RuntimeProposalStatus Status,
    string ProposedSpec,
    string? AppliedSpec,
    string? Reason,
    string? DecidedBy,
    DateTime? DecidedAt,
    string? ErrorMessage,
    DateTime CreatedAt);
