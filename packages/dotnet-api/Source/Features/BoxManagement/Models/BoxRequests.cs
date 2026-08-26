using System.Text.Json.Serialization;

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Body for <c>POST /boxes</c>. Only used by admin/template flows — normal
/// runtime provisioning forks the golden template instead (see
/// <see cref="ForkBoxRequest"/>), which inherits the whole prepared filesystem.
///
/// <para>Per the OpenAPI contract the create body has NO <c>name</c> field —
/// naming happens after create via <c>PATCH /boxes/{id}</c>
/// (<see cref="UpdateBoxRequest.Name"/>). The machine tier field is
/// <c>type</c> (<c>small</c>/<c>default</c>/<c>large</c>).</para>
///
/// <para><b>Env keys pass through verbatim.</b> They're the daemon's contract
/// (<c>RUNTIME_ID</c>, <c>MAIN_API_URL</c>, ...) — the serialiser must never
/// case-convert dictionary keys, only property names.</para>
/// </summary>
public record CreateBoxRequest(
    string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null,
    Dictionary<string, string>? Env = null,
    string? Environment = null,
    bool? NoEnv = null,
    string? SetupScript = null,
    string? Org = null,
    string? From = null);

/// <summary>
/// Body for <c>POST /boxes/{id}/fork</c> — the platform's provisioning
/// primitive. Same shape as create minus <c>from</c>/<c>setupScript</c>; there
/// is no <c>name</c> field either — forks are named after the fact via
/// <c>PATCH /boxes/{id}</c>. A fork inherits the source box's entire disk
/// (latest snapshot) and replaces inherited env vars with the ones given here.
/// <c>type</c> inherits the source's tier unless specified — runtime forks
/// always pass the mapped tier explicitly.
///
/// <para><b><c>NoEnv</c> is always true for runtime forks.</b> A no-env box
/// receives none of the platform account's secrets and cannot act on the
/// account or on other boxes — the runtime gets exactly the env we stamp,
/// nothing more. This is Box's documented isolation model for platforms.</para>
/// </summary>
public record ForkBoxRequest(
    string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null,
    Dictionary<string, string>? Env = null,
    string? Environment = null,
    bool? NoEnv = true,
    string? Org = null);

/// <summary>
/// Body for <c>PATCH /boxes/{id}</c>: <c>ttlSeconds</c> (the orphan-cost
/// guardrail re-arm — see <c>BoxTtlExtenderJob</c>), <c>name</c> (1–120 chars;
/// the ONLY way to name a box, since create/fork accept no name), and
/// <c>subdomain</c>. Null fields are omitted from the body.
/// </summary>
public record UpdateBoxRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null,
    string? Name = null,
    string? Subdomain = null);

/// <summary>
/// Optional body for <c>POST /boxes/{id}/resume</c>. Lets a resume apply a new
/// tier (<c>type</c>), replacement env, and a fresh TTL in the same machine
/// start — the reboot/resize paths use this instead of resume-then-commands
/// (the commands-based <c>/etc/glenn/runtime.env</c> refresh stays as belt and
/// braces, since systemd reads the env file).
/// </summary>
public record ResumeBoxRequest(
    string? Type = null,
    Dictionary<string, string>? Env = null,
    string? Environment = null,
    bool? NoEnv = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TtlSeconds = null);

/// <summary>
/// Optional body for <c>POST /boxes/{id}/stop</c>.
/// </summary>
public record StopBoxRequest(bool? Force = null);

/// <summary>
/// Body for <c>POST /boxes/{id}/command</c> (SINGULAR) — run an arbitrary shell
/// command inside the box. Used by admin/debug surfaces and the repair loop's
/// daemon-independent side channel; NOT used on the normal boot path (the
/// daemon drives everything once it's up). <c>TimeoutSeconds</c> is 1–600
/// (contract default 30) — long provisioning steps must pass an explicit value.
/// </summary>
public record RunBoxCommandRequest(
    string Command,
    string? Cwd = null,
    int? TimeoutSeconds = null,
    bool? Detached = null);

/// <summary>
/// Response of <c>POST /boxes/{id}/command</c> per the OpenAPI contract:
/// <c>{ exitCode, stdout, stderr, timedOut, stdoutTruncated, stderrTruncated,
/// startedAt, finishedAt }</c>.
/// </summary>
public record RunBoxCommandResponse(
    int? ExitCode,
    string? Stdout,
    string? Stderr,
    bool? TimedOut,
    bool? StdoutTruncated,
    bool? StderrTruncated,
    DateTime? StartedAt,
    DateTime? FinishedAt)
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
/// A disk snapshot summary as returned by the PER-BOX snapshot endpoints
/// (<c>GET /boxes/{boxId}/snapshots</c> / <c>GET /boxes/{boxId}/snapshots/latest</c>).
/// The contract exposes no account-level snapshot listing. Snapshots are the
/// persistence layer behind stop/resume and the source material for forks.
/// Wire fields per SnapshotSummary: id, status, generation, createdAt,
/// sizeBytes, fileCount, contentSizeBytes (no boxId — callers know which box
/// they asked about).
/// </summary>
public record BoxSnapshot(
    string Id,
    string? Status,
    long? Generation,
    DateTime? CreatedAt,
    long? SizeBytes,
    long? FileCount,
    long? ContentSizeBytes)
{
    /// <summary>
    /// Overflow bag for unmodelled wire fields. Body property, not a
    /// primary-constructor parameter — System.Text.Json cannot bind
    /// <see cref="JsonExtensionData"/> through a deserialization constructor.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
