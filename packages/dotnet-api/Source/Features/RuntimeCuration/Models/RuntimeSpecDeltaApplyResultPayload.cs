using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// Daemon-to-server ack for <see cref="ApplyRuntimeSpecDeltaPayload"/>. Sent
/// over the <see cref="SignalR.Hubs.RuntimeHub.RuntimeSpecDeltaApplied"/> hub
/// method after the daemon finishes (or fails) the mise/supervisord work for a
/// given proposal.
///
/// <list type="bullet">
///   <item><see cref="ProposalId"/> echoes the value the server pushed in
///         <see cref="ApplyRuntimeSpecDeltaPayload.ProposalId"/> — the only way
///         the server correlates an ack back to a row.</item>
///   <item><see cref="Success"/>=true → flip to
///         <see cref="RuntimeProposalStatus.Applied"/>, clear
///         <see cref="RuntimeProposal.ErrorMessage"/>.</item>
///   <item><see cref="Success"/>=false → flip to
///         <see cref="RuntimeProposalStatus.Failed"/>, persist
///         <see cref="Error"/> on the row.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record RuntimeSpecDeltaApplyResultPayload(
    Guid ProposalId,
    bool Success,
    string? Error);
