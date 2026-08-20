using System.Text.Json.Serialization;

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Body for <c>POST /boxes</c>. Only used by admin/template flows — normal
/// runtime provisioning forks the golden template instead (see
/// <see cref="ForkBoxRequest"/>), which inherits the whole prepared filesystem.
///
/// <para><b>Env keys pass through verbatim.</b> They're the daemon's contract
/// (<c>RUNTIME_ID</c>, <c>MAIN_API_URL</c>, ...) — the serialiser must never
/// case-convert dictionary keys, only property names.</para>
/// </summary>
public record CreateBoxRequest(
    string? Name = null,
    string? Size = null,
    Dictionary<string, string>? Env = null,
    bool? NoEnv = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null);

/// <summary>
/// Body for <c>POST /boxes/{id}/fork</c> — the platform's provisioning
/// primitive. A fork inherits the source box's entire disk (latest snapshot)
/// and replaces inherited env vars with the ones given here.
///
/// <para><b><c>NoEnv</c> is always true for runtime forks.</b> A no-env box
/// receives none of the platform account's secrets and cannot act on the
/// account or on other boxes — the runtime gets exactly the env we stamp,
/// nothing more. This is Box's documented isolation model for platforms.</para>
/// </summary>
public record ForkBoxRequest(
    string? Name = null,
    string? Size = null,
    Dictionary<string, string>? Env = null,
    bool? NoEnv = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null);

/// <summary>
/// Body for <c>PATCH /boxes/{id}</c>. Today we only ever patch the TTL — the
/// orphan-cost guardrail re-arm (see <c>BoxTtlExtenderJob</c>).
/// </summary>
public record UpdateBoxRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null);

/// <summary>
/// Body for <c>POST /boxes/{id}/commands</c> — run an arbitrary shell command
/// inside the box. Used by admin/debug surfaces and the repair loop's
/// daemon-independent side channel; NOT used on the normal boot path (the
/// daemon drives everything once it's up).
/// </summary>
public record RunBoxCommandRequest(string Command);

/// <summary>Result envelope for a box command run. Stringly-typed; shape kept minimal.</summary>
public record RunBoxCommandResponse(
    string? Stdout,
    string? Stderr,
    int? ExitCode)
{
    /// <summary>
    /// Overflow bag for unmodelled wire fields. Body property, not a
    /// primary-constructor parameter — System.Text.Json cannot bind
    /// <see cref="JsonExtensionData"/> through a deserialization constructor.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}

/// <summary>
/// A disk snapshot as returned by <c>GET /snapshots</c>. Snapshots are the
/// persistence layer behind stop/resume and the source material for forks;
/// the admin cleanup page lists and deletes orphaned ones.
/// </summary>
public record BoxSnapshot(
    string Id,
    string? BoxId,
    DateTime? CreatedAt,
    long? SizeBytes)
{
    /// <summary>
    /// Overflow bag for unmodelled wire fields. Body property, not a
    /// primary-constructor parameter — System.Text.Json cannot bind
    /// <see cref="JsonExtensionData"/> through a deserialization constructor.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
