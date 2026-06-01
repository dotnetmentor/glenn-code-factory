using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// Wire-shape payload broadcast to the <c>project-{ProjectId}</c> SignalR group
/// every time the daemon successfully calls <c>POST /api/runtimes/{id}/proposals</c>
/// (i.e. invokes its <c>propose_runtime_spec</c> custom tool). Drives the
/// confirmation-card UI in the chat panel — the user sees Approve / Edit /
/// Reject directly from this notification, without a round-trip fetch.
///
/// <para><b>Why a flat string for <see cref="ProposedSpec"/>.</b> The spec is
/// stored as opaque <c>jsonb</c> on <see cref="RuntimeProposal.ProposedSpec"/>
/// and we re-emit it verbatim on the wire. Re-shaping it into a typed nested
/// record here would (a) duplicate the schema in two places, (b) force schema
/// evolution to land on the backend before the frontend can render, and (c)
/// gain us nothing — the React client parses the JSON itself when rendering
/// the card. Same rationale as <c>AgentEventNotification.EventData</c>.</para>
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the record to
/// Tapper's TypeScript generator so the daemon-/frontend-facing TS types are
/// kept in lockstep with the C# definition.</para>
/// </summary>
[TranspilationSource]
public record RuntimeProposalCreatedPayload(
    Guid ProposalId,
    Guid RuntimeId,
    Guid ProjectId,
    string ProposedSpec,
    string Reason);
