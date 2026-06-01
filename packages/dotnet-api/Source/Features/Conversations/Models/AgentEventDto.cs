using System.Text.Json.Serialization;
using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Polymorphic wire shape returned by <c>GET /api/sessions/{id}/events</c> — a
/// discriminated union over Cursor's <c>SDKMessage</c> kinds. The chat panel
/// reads these rows in <see cref="Sequence"/>-ascending order to render the
/// conversation; each subtype surfaces every first-class column relevant to
/// its kind so the frontend never has to parse opaque JSON.
///
/// <para><b>Discriminator</b> (System.Text.Json polymorphism). The
/// <c>eventKind</c> property is emitted on the wire as the first JSON key for
/// every payload — the frontend switches on this to narrow to the concrete
/// subtype. We use camelCase string discriminators (<c>"toolUse"</c>,
/// <c>"thinking"</c>, ...) so the React layer can switch directly on the
/// value without a remapping table; ordinal stability across reorderings is
/// the same convention used for <see cref="AgentEventKind"/> on the wire.</para>
///
/// <para><b>Why polymorphic on the wire?</b> The alternative — one mega-DTO
/// with every-kind columns nullable — works at the storage layer but ships a
/// Cartesian-product type to the frontend that's awkward to render. The
/// discriminator pattern surfaces the same data but lets the frontend write
/// exhaustive switches and gets proper TypeScript narrowing under the
/// generated Orval hooks.</para>
///
/// <para>The shared fields (<see cref="SessionId"/>, <see cref="Sequence"/>,
/// <see cref="CreatedAt"/>) are duplicated onto every concrete subtype's
/// constructor for clarity in the generated TypeScript — System.Text.Json's
/// polymorphic serializer needs the discriminator on the abstract base, but
/// the subtypes are what land on the wire.</para>
/// </summary>
[TranspilationSource]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventKind")]
[JsonDerivedType(typeof(PromptReceivedEventDto), "promptReceived")]
[JsonDerivedType(typeof(AssistantTextEventDto), "assistantText")]
[JsonDerivedType(typeof(ThinkingEventDto), "thinking")]
[JsonDerivedType(typeof(ToolUseEventDto), "toolUse")]
[JsonDerivedType(typeof(StatusEventDto), "status")]
[JsonDerivedType(typeof(TaskEventDto), "task")]
public abstract record AgentEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.PromptReceived"/> — the user's prompt landed at
/// the daemon. First row of a session at <c>Sequence = 0</c>. <see cref="Text"/>
/// carries the prompt body verbatim.
/// </summary>
[TranspilationSource]
public record PromptReceivedEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    string Text) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.AssistantText"/> — a chunk of assistant-visible
/// text. <see cref="Text"/> carries the body; the chat panel concatenates
/// consecutive AssistantText rows of the same session to render the assistant
/// turn.
/// </summary>
[TranspilationSource]
public record AssistantTextEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    string Text) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.Thinking"/> — extended-thinking chunk.
/// <see cref="Text"/> carries the body; <see cref="ThinkingDurationMs"/> is
/// populated on the terminal frame of a thinking burst so the chat panel can
/// render "Thought for 3.4s".
/// </summary>
[TranspilationSource]
public record ThinkingEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    string Text,
    long? ThinkingDurationMs) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.ToolUse"/> — a tool invocation. The
/// <see cref="CallId"/> pairs the running row with its terminal (completed /
/// error) row so the chat panel can collapse the pair into a single tool card.
///
/// <para><see cref="Args"/> and <see cref="Result"/> are raw JSON strings (the
/// underlying Postgres column is <c>jsonb</c>); each tool formatter on the
/// frontend re-parses on read because the shape varies per tool. Carried as
/// strings on the wire because System.Text.Json can't transpile
/// <c>JsonElement</c> through Tapper directly — same convention as
/// <c>RuntimeEventPayloadDto.Payload</c>.</para>
/// </summary>
[TranspilationSource]
public record ToolUseEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    string CallId,
    string Name,
    AgentEventToolStatus Status,
    string? Args,
    string? Result,
    bool ArgsTruncated,
    bool ResultTruncated) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.Status"/> — run-level lifecycle transition. The
/// chat panel's persistent activity pill reads <see cref="Status"/> to render
/// the headline state; <see cref="Message"/> carries any accompanying
/// human-readable string (typically present on Error transitions).
/// </summary>
[TranspilationSource]
public record StatusEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    AgentEventRunStatus Status,
    string? Message) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// <see cref="AgentEventKind.Task"/> — milestone divider. The chat panel
/// renders these as section headers inside the assistant turn.
/// </summary>
[TranspilationSource]
public record TaskEventDto(
    Guid SessionId,
    long Sequence,
    DateTime CreatedAt,
    string? TaskId,
    string? Title) : AgentEventDto(SessionId, Sequence, CreatedAt);

/// <summary>
/// One artifact entry inside a <see cref="RunResultDto"/>. Mirrors Cursor SDK's
/// <c>SDKArtifact</c> shape — the file path the agent created / edited /
/// deleted, its size on disk, and the modification timestamp. Re-parsed
/// per-render on the frontend (no need to project into a richer type — the
/// chat panel just counts entries for the "N files edited" chip and links
/// each entry into the existing diff viewer).
/// </summary>
[TranspilationSource]
public record ArtifactDto(
    string Path,
    long SizeBytes,
    DateTime UpdatedAt);

/// <summary>
/// Per-turn aggregate result — one row per <see cref="AgentSession"/> when the
/// run reaches a terminal state. Drives the chat panel's turn footer
/// (<i>"Finished in 14.2s · claude-sonnet-4 · 5 files edited · view PR ↗"</i>).
///
/// <para>Returned alongside the event list when paging through a completed
/// session, or fetched via the dedicated <c>GET /api/sessions/{id}/run-result</c>
/// endpoint.</para>
///
/// <para><see cref="Artifacts"/> is the materialized list of files the agent
/// touched — the raw <c>SDKArtifact[]</c> from <c>agent.listArtifacts()</c>
/// projected into <see cref="ArtifactDto"/>. Empty list when the run didn't
/// touch any files.</para>
/// </summary>
[TranspilationSource]
public record RunResultDto(
    Guid SessionId,
    long DurationMs,
    string Model,
    string? GitBranch,
    string? GitPrUrl,
    IReadOnlyList<ArtifactDto> Artifacts,
    DateTime CreatedAt);
