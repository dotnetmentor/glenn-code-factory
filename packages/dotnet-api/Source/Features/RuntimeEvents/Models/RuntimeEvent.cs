using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeEvents.Models;

/// <summary>
/// Append-only structured event row capturing a single thing that happened
/// inside a runtime — a bootstrap stage transition, an install snippet running,
/// a supervised service crashing, a spec delta being applied. Powers the
/// runtime drawer's Timeline tab and the future "why is boot slow today?"
/// observability story.
///
/// <para><b>Why a dedicated table.</b> The existing <c>RuntimeStateEvent</c>
/// audit row captures only the lifecycle state-machine (Pending → Booting →
/// Online → …). The V2 spec's event taxonomy is much richer: install / setup /
/// service-level events that the state machine never sees. Rather than
/// overloading <c>RuntimeStateEvent</c> we keep audit rows narrow and put the
/// freeform telemetry here, where retention rules and indexing can be tuned
/// independently.</para>
///
/// <para><b>Append-only, no FK to <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>.</b>
/// Same convention as <c>RuntimeStateEvent</c> / <c>RuntimeErrorReport</c> /
/// <c>BootstrapRun</c>: runtimes can be hard-deleted, but the diagnostic
/// trail must outlive them. <see cref="RuntimeId"/> is a plain Guid we
/// record.</para>
///
/// <para><b>Rolling FIFO cap.</b> The
/// <see cref="Commands.RecordRuntimeEventCommandHandler"/> enforces a per-runtime
/// cap (5000) after every insert. The Timeline only needs the last few hundred
/// events anyway, so the older rows are dropped without ceremony.</para>
///
/// <para><b>Timing as a first-class column.</b> Per the spec's "Timing — first-class
/// concern" section, every <c>*Completed</c> / <c>*Failed</c> / <c>*Skipped</c>
/// event carries a <c>durationMs</c> in its payload. We promote that to a
/// top-level nullable column so the API can sort by "slowest installs" or
/// "slowest setup commands" without parsing JSON in the query plan. The
/// payload still keeps the canonical value for cross-checking.</para>
///
/// <para>POCO — no business methods. Inserts and cap-enforcement live in
/// <see cref="Commands.RecordRuntimeEventCommand"/>; reads live in queries
/// added by sibling cards (REST + SignalR push are Card P3.2).</para>
/// </summary>
public class RuntimeEvent : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The runtime this event belongs to. Plain Guid (no FK) so events
    /// survive a hard-delete of the runtime. Indexed via the composite
    /// <c>(RuntimeId, Timestamp DESC)</c> in <c>OnModelCreating</c>.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Event type string — one of the constants in <see cref="RuntimeEventTypes"/>.
    /// Stored as varchar (not an enum) so the daemon can emit new types without
    /// a coordinated migration on the backend. Indexed via the
    /// <c>(RuntimeId, Type, Timestamp DESC)</c> composite for filtered reads
    /// ("show me only the InstallFailed events for runtime X").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Severity classification for drawer rendering and unread-badge logic.
    /// Persisted as a string via <c>HasConversion&lt;string&gt;()</c> for DB
    /// readability and to keep adding new severities safe.
    /// </summary>
    public RuntimeEventSeverity Severity { get; set; }

    /// <summary>
    /// UTC timestamp emitted by the daemon — the source of truth for "when
    /// did this happen?". Distinct from <see cref="IAuditable.CreatedAt"/>,
    /// which the backend stamps at row insert. The drawer's Timeline orders
    /// on <see cref="Timestamp"/>, not <c>CreatedAt</c>, so a daemon that
    /// batches events still renders them in domain order. Stored as
    /// <c>timestamp with time zone</c>.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Duration in milliseconds, promoted from the payload to a top-level
    /// column so we can sort / filter without parsing jsonb. Null for
    /// <c>*Started</c> events (no duration yet) and for events that have no
    /// natural pairing (e.g. a one-shot <c>SpecValidationFailed</c>). Indexed
    /// for non-null values via <c>(RuntimeId, DurationMs DESC) WHERE DurationMs IS NOT NULL</c>.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Full structured payload as a JSON string stored in a Postgres <c>jsonb</c>
    /// column. Shape depends on <see cref="Type"/> — install events carry
    /// <c>{ hash, snippet, ... }</c>, service events carry <c>{ name, exitCode,
    /// restartCount, ... }</c>, spec delta events carry
    /// <c>{ phaseTimings: { installMs, servicesMs, setupMs }, ... }</c>.
    ///
    /// <para>Stored as <see cref="string"/> rather than <see cref="System.Text.Json.JsonDocument"/>
    /// because (a) the EF InMemory provider used in tests can't map
    /// <c>JsonDocument</c>, (b) this matches the convention every other
    /// jsonb-backed column in the codebase uses (<c>ProjectRuntime.Spec</c>,
    /// <c>RuntimeProposal.ProposedSpec</c>, <c>StoredDomainEvent.Payload</c>,
    /// <c>McpServer.Metadata</c>, …). Callers should serialise with
    /// <c>JsonSerializer.Serialize(...)</c>; the daemon hub method that
    /// receives wire payloads can pass them through verbatim.</para>
    /// </summary>
    public string Payload { get; set; } = "{}";

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
