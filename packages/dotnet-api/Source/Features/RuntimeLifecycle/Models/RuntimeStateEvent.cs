using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Append-only audit row capturing a single lifecycle state transition on a
/// <see cref="ProjectRuntime"/>. Every move through the
/// <see cref="RuntimeState"/> graph — Pending → Booting, Online → Suspending,
/// Crashed → Failed, etc. — gets exactly one row here so we can:
///
/// <list type="bullet">
///   <item>replay how a runtime got into its current state;</item>
///   <item>diagnose why a transition fired (idler? webhook? operator reset?);</item>
///   <item>build dashboards / alerts off the historical timeline.</item>
/// </list>
///
/// <para>Deliberately NOT soft-deletable and intentionally has NO EF foreign
/// key to <see cref="ProjectRuntime"/> — the audit trail must outlive the
/// runtime row even after a hard-delete. <see cref="RuntimeId"/> is a plain
/// Guid we record, mirroring the <c>FlyOperation.RuntimeId</c> /
/// <c>BootstrapRun.RuntimeId</c> convention.</para>
///
/// <para>POCO on purpose. The <c>TransitionTo</c> method on
/// <see cref="ProjectRuntime"/> and the event handler that writes these rows
/// arrive in a follow-up card.</para>
/// </summary>
public class RuntimeStateEvent : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The runtime this transition belongs to. Plain Guid (no FK) so audit
    /// rows survive a hard-delete of the runtime. Indexed.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// State the runtime moved away from. Nullable because the very first row
    /// for a runtime — the Pending insert — has no prior state.
    /// </summary>
    public RuntimeState? FromState { get; set; }

    /// <summary>State the runtime moved into. Required.</summary>
    public RuntimeState ToState { get; set; }

    /// <summary>
    /// Short machine-readable trigger code, e.g. <c>"fly_webhook:machine.started"</c>,
    /// <c>"idler:exceeded_threshold"</c>, <c>"operator:reset"</c>. Free-form
    /// so new triggers don't require a schema change.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Who/what caused the transition, e.g. <c>"system:provisioner"</c>,
    /// <c>"user:abc-123"</c>, <c>"fly:webhook"</c>. Free-form.
    /// </summary>
    public string TriggeredBy { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON blob with per-transition extras (Fly request id, idle
    /// minutes observed, the previous heartbeat timestamp, etc.). <c>null</c>
    /// when nothing extra is worth recording.
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
