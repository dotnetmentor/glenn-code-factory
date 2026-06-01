using Source.Features.RuntimeBootstrap.Contracts;
using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// Server-to-daemon command instructing the daemon to apply a runtime-spec
/// delta — pushed over <see cref="SignalR.Hubs.IRuntimeClient"/> to the
/// <c>runtime-{RuntimeId}</c> group after a user approves (or edits) a
/// <see cref="RuntimeProposal"/>. The daemon is responsible for installing
/// new services, restarting changed ones, and (in future phases) tearing
/// down removed ones. <see cref="ProposalId"/> is echoed back on the ack so
/// the server can flip the proposal row to <see cref="RuntimeProposalStatus.Applied"/>
/// or <see cref="RuntimeProposalStatus.Failed"/>.
///
/// <para><b>Why an envelope around <see cref="RuntimeSpecDeltaV2"/>.</b> The
/// SignalR method name (<c>ApplyRuntimeSpecDelta</c>) and the correlating
/// proposal id are infrastructure concerns that live alongside the spec
/// itself. The diff lives in <see cref="Delta"/> — daemon-side handling can
/// switch on its fields (<see cref="RuntimeSpecDeltaV2.NewOrChangedServices"/>,
/// <see cref="RuntimeSpecDeltaV2.RemovedServices"/>, install/setup hashes)
/// to decide what to actually do.</para>
///
/// <para><b>Empty delta.</b> A proposal that's already fully covered by the
/// current spec produces <see cref="RuntimeSpecDeltaV2.HasChanges"/>=false;
/// the daemon ack-only no-ops it and the proposal row still flips to
/// Applied. Same semantics as the V1 empty-arrays case.</para>
/// </summary>
[TranspilationSource]
public record ApplyRuntimeSpecDeltaPayload(
    Guid ProposalId,
    RuntimeSpecDeltaV2 Delta);
