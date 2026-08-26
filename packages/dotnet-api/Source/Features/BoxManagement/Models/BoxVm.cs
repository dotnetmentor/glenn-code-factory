using System.Text.Json.Serialization;

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Subset of the Box resource (<c>GET /boxes/{id}</c>, unwrapped from the
/// <c>{"ok":true,"type":"box.info","box":{...}}</c> envelope) we surface to
/// callers. CamelCase fields on the wire are mapped via the
/// <see cref="BoxClient"/>'s camelCase serialiser settings.
///
/// <para>The lifecycle field on the wire is <c>state</c> (NOT <c>status</c>).
/// State values are deliberately stringly-typed: the OpenAPI contract's enum
/// today is <c>init</c>, <c>provisioning</c>, <c>provisioned</c>, <c>cloning</c>,
/// <c>ready</c>, <c>idle</c>, <c>running</c>, <c>archiving</c>, <c>archived</c>,
/// <c>error</c>, and forcing a closed enum here would just break us on the next
/// addition. Use <see cref="BoxStates"/> for the canonical constants and the
/// "is it up / is it down" helpers.</para>
/// </summary>
public record BoxVm(
    string Id,
    string? Name,
    string State,
    string? Type,
    string? Region,
    long? TtlSeconds,
    DateTime? CreatedAt)
{
    /// <summary>
    /// Overflow bag for wire fields we don't model. Declared as a body property —
    /// NOT a primary-constructor parameter — because System.Text.Json cannot bind
    /// a <see cref="JsonExtensionData"/> property through a deserialization
    /// constructor (it throws <c>InvalidOperationException</c> on first use).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

/// <summary>
/// Canonical Box <c>state</c> strings (per the authoritative OpenAPI contract)
/// plus grouping helpers. A box is either up (<c>ready</c>/<c>idle</c>/
/// <c>running</c>), transitional — still coming up or winding down
/// (<c>init</c>/<c>provisioning</c>/<c>provisioned</c>/<c>cloning</c>/
/// <c>archiving</c>), stopped-with-snapshot (<c>archived</c>), or broken
/// (<c>error</c>). There is no separate "destroyed" state — a deleted box simply
/// stops existing (404 / absent from the list), which the reconciler treats via
/// its machine-missing branch.
/// </summary>
public static class BoxStates
{
    public const string Init = "init";
    public const string Provisioning = "provisioning";
    public const string Provisioned = "provisioned";
    public const string Cloning = "cloning";
    public const string Ready = "ready";
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Archiving = "archiving";
    public const string Archived = "archived";
    public const string Error = "error";

    /// <summary>The box VM is up and can execute work (daemon may or may not be connected yet).</summary>
    public static bool IsUp(string? state) =>
        state?.ToLowerInvariant() is Ready or Idle or Running;

    /// <summary>The box is stopped with its disk snapshotted — billing paused, resumable.</summary>
    public static bool IsArchived(string? state) =>
        state?.ToLowerInvariant() is Archived;

    /// <summary>The box is mid-transition (create/fork/resume/stop in flight) — let it settle.</summary>
    public static bool IsTransitional(string? state) =>
        state?.ToLowerInvariant() is Init or Provisioning or Provisioned or Cloning or Archiving;

    /// <summary>Box-side hard failure.</summary>
    public static bool IsError(string? state) =>
        state?.ToLowerInvariant() is Error;
}
