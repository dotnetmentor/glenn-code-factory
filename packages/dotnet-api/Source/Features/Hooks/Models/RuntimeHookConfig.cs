using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Hooks.Models;

/// <summary>
/// Per-runtime hook configuration. One row per <see cref="RuntimeLifecycle.Models.ProjectRuntime"/>
/// (enforced by a unique index on <see cref="RuntimeId"/>) carrying the daemon's
/// hook config as opaque jsonb.
///
/// <list type="bullet">
///   <item>The daemon owns the schema. The server stores + relays the bytes
///         verbatim; the only validation here is the top-level shape check
///         in the admin endpoint (object with the four required arrays).
///         Per-hook fields (<c>cmd</c>, <c>feedbackMode</c>, <c>pattern</c>, …)
///         are the daemon's contract, not ours — adding fields server-side
///         would force a coordinated deploy on every shape change.</item>
///   <item>FK to <c>ProjectRuntime</c> on <see cref="RuntimeId"/> with
///         <c>NoAction</c>: runtimes are soft-deleted, and a stale config row
///         is harmless (no daemon to push to). The 30-day janitor window
///         doubles as the cleanup path.</item>
///   <item>Soft-deletable so operators can hide a row without losing the audit
///         trail (and so the global query filter applies).</item>
///   <item>Admin path is the only writer. The bootstrap delivery in
///         <c>RuntimeHub.OnConnectedAsync</c> reads <see cref="Json"/> and
///         pushes it to the connecting daemon as a one-shot
///         <c>UpdateConfig</c>; subsequent edits route through the
///         admin endpoint, which both persists here and pushes the same way.</item>
/// </list>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change — even though this card has none.</para>
/// </summary>
public class RuntimeHookConfig : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Runtime this config applies to. Unique index — one config per runtime.
    /// FK to <c>ProjectRuntime</c> with <c>NoAction</c> for the same outlive-the
    /// -row reasoning as <see cref="HookExecution.RuntimeId"/>.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Raw hook configuration JSON, persisted as <c>jsonb</c>. Defaults to
    /// <c>"{}"</c> so the column never carries SQL <c>NULL</c> — empty config
    /// is "no hooks configured", not "value missing". The daemon parses; the
    /// server only validates the top-level shape on write.
    /// </summary>
    public string Json { get; set; } = "{}";

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
