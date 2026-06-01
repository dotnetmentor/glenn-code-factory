namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Wire-shape projection of a single <see cref="RuntimeStateEvent"/> row used by both the
/// user-facing status query and the operator detail view. Fields are exactly the audit
/// columns operators / users care about — id and runtime id are deliberately omitted
/// because the caller already knows which runtime they're inspecting.
///
/// <para><see cref="OccurredAt"/> is the audit row's <c>CreatedAt</c> renamed for the
/// API: callers think in terms of "when did the transition happen", not "when was the
/// audit row inserted". (For this append-only table the two are identical, but the
/// renamed field documents intent.)</para>
/// </summary>
public record RuntimeTransitionDto(
    RuntimeState? FromState,
    RuntimeState ToState,
    string Reason,
    string TriggeredBy,
    DateTime OccurredAt);
