using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Daemon-to-server <i>and</i> server-to-UI fan-out: the daemon received a
/// <c>StartTurn</c> for a session while another turn is still in flight on the
/// same runtime. The single-turn invariant lives on the daemon side
/// (<c>TurnRunner.start()</c>); the daemon refuses by sending this payload up
/// to <c>RuntimeHub.TurnRefused</c>, which (a) flips the rejected
/// <see cref="AgentSession"/> to <see cref="AgentSessionStatus.Failed"/> with
/// <c>CancelReason="daemon_refused_concurrent"</c>, and (b) re-broadcasts the
/// same payload to the project's UI clients via
/// <see cref="Source.Features.SignalR.Hubs.IAgentClient.TurnRefused"/> so the
/// chat panel can render a "we couldn't start that turn — another is still
/// running" affordance without having to poll session state.
///
/// <list type="bullet">
///   <item><see cref="SessionId"/> is the rejected session — the one the
///         daemon just refused to run.</item>
///   <item><see cref="Reason"/> is a short stable token for the chat UI's
///         copy ladder. Today the daemon only ever sends
///         <c>"turn_already_running"</c>; the field is a string instead of an
///         enum so the daemon can introduce new reasons without a wire
///         break.</item>
///   <item><see cref="CurrentSessionId"/> is the session the daemon is
///         <i>currently</i> running, when known. Useful for the UI to point
///         the user at the in-flight turn ("waiting on session X to finish").
///         Nullable because a daemon mid-state-transition may not have a
///         single clear answer.</item>
/// </list>
///
/// <para>Direction note: this lives in the <c>Conversations</c> feature folder
/// rather than in <c>SignalR/Contracts</c> because it's a domain-shaped
/// payload (bound to <see cref="AgentSession"/>) shared by both directions of
/// the hub. Importing the type from a single canonical home keeps the
/// generated TypeScript single-sourced.</para>
/// </summary>
[TranspilationSource]
public record TurnRefusedPayload(Guid SessionId, string Reason, Guid? CurrentSessionId);
