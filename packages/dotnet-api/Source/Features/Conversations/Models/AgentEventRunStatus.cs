using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Run-level lifecycle status carried on a Cursor <c>SDKStatusMessage</c> row
/// (an <see cref="AgentEvent"/> where <see cref="AgentEvent.Kind"/> is
/// <see cref="AgentEventKind.Status"/>). Mirrors Cursor SDK's
/// <c>SDKStatusMessage.status</c> values. Persisted as a string for wire
/// stability across enum reorderings — adding a new state must not silently
/// shift existing rows.
///
/// <para>The chat UI's persistent activity pill (see <c>cursor-native-chat-ux</c>
/// spec §3) reads these values directly to render the headline state. Every
/// terminal value (<see cref="Finished"/>, <see cref="Error"/>,
/// <see cref="Cancelled"/>, <see cref="Expired"/>) freezes the pill in its
/// last state.</para>
/// </summary>
[TranspilationSource]
public enum AgentEventRunStatus
{
    /// <summary>Agent is being created — first transient state, before <see cref="Running"/>.</summary>
    Creating = 0,

    /// <summary>Agent is actively processing the turn.</summary>
    Running = 1,

    /// <summary>Agent finished the turn successfully — terminal.</summary>
    Finished = 2,

    /// <summary>Agent stopped due to an unrecoverable error — terminal.</summary>
    Error = 3,

    /// <summary>User cancelled the turn — terminal.</summary>
    Cancelled = 4,

    /// <summary>Agent ran past its deadline / lifetime — terminal.</summary>
    Expired = 5,
}
