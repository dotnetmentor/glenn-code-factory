using System.Text.Json.Serialization;

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Subset of the Box resource (<c>GET /boxes/{id}</c>) we surface to callers.
/// CamelCase fields on the wire are mapped via the <see cref="BoxClient"/>'s
/// camelCase serialiser settings.
///
/// <para>Status values are deliberately stringly-typed: the documented set today is
/// <c>provisioning</c>, <c>ready</c>, <c>idle</c>, <c>running</c>, <c>archived</c>,
/// <c>error</c>, and forcing a closed enum here would just break us on the next
/// addition. Use <see cref="BoxStatus"/> for the canonical constants and the
/// "is it up / is it down" helpers.</para>
/// </summary>
public record BoxVm(
    string Id,
    string? Name,
    string Status,
    string? Size,
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
/// Canonical Box status strings plus grouping helpers. Box's states are coarser
/// than Fly's were: a box is either coming up (<c>provisioning</c>), up
/// (<c>ready</c> / <c>idle</c> / <c>running</c>), stopped-with-snapshot
/// (<c>archived</c>), or broken (<c>error</c>). There is no separate
/// "destroyed" state — a deleted box simply stops existing (404 / absent from
/// the list), which the reconciler treats via its machine-missing branch.
/// </summary>
public static class BoxStatus
{
    public const string Provisioning = "provisioning";
    public const string Ready = "ready";
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Archived = "archived";
    public const string Error = "error";

    /// <summary>The box VM is up and can execute work (daemon may or may not be connected yet).</summary>
    public static bool IsUp(string? status) =>
        status?.ToLowerInvariant() is Ready or Idle or Running;

    /// <summary>The box is stopped with its disk snapshotted — billing paused, resumable.</summary>
    public static bool IsArchived(string? status) =>
        status?.ToLowerInvariant() is Archived;

    /// <summary>The box is still coming up (create/fork/resume in flight).</summary>
    public static bool IsProvisioning(string? status) =>
        status?.ToLowerInvariant() is Provisioning;

    /// <summary>Box-side hard failure.</summary>
    public static bool IsError(string? status) =>
        status?.ToLowerInvariant() is Error;
}
