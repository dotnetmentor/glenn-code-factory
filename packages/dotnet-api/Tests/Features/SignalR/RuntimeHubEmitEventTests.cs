namespace Api.Tests.Features.SignalR;

/// <summary>
/// TODO(card 3 / cursor-native-chat-ux): the entire pre-Cursor wire-shape
/// test surface was wiped here. The previous tests pinned the EmitEvent
/// behaviour to the Anthropic-shaped vocabulary (TurnStarted / TurnCompleted /
/// TurnFailed / TurnCanceled / SystemMessage / AssistantText / ToolCall) and
/// the JSON-blob payloads that went with it. Card 2 (this card) wipes that
/// schema and replaces it with the Cursor SDK's <c>SDKMessage</c> shape
/// (Status + RunStatus discriminator; first-class Text / ToolName / Args /
/// Result / etc. columns). The status state-machine is now driven by
/// <c>Kind == Status &amp;&amp; RunStatus == Finished</c> / <c>Error</c> /
/// <c>Cancelled</c> / <c>Expired</c>, NOT by a top-level TurnCompleted /
/// TurnFailed / TurnCanceled discriminator.
///
/// <para>Card 3 (chat-history-rewrite) re-introduces this test suite under
/// the new vocabulary — every behaviour previously covered here (agent-id
/// capture, terminal transitions, queue-drain on terminal, audit-row
/// persistence, broadcast fan-out) needs a Cursor-shaped equivalent.
/// Re-creating those tests against a half-finished hub would just churn the
/// suite a second time, so we leave this file empty until the hub side
/// settles.</para>
///
/// <para>Keep the file (rather than deleting) so any future search for
/// "RuntimeHubEmitEventTests" still lands here with the breadcrumb pointing
/// at the in-flight card.</para>
/// </summary>
internal static class RuntimeHubEmitEventTestsPlaceholder
{
}
