using Source.Features.Conversations.Models;
using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// Daemon-to-server heartbeat. Pushed by every running daemon on a fixed cadence
/// (the daemon side of the heartbeat-respawn spec; today ~every 5 seconds) so
/// the server can:
/// <list type="bullet">
///   <item>refresh <c>ProjectRuntime.LastHeartbeatAt</c> for idle / staleness
///         detection — the per-runtime <i>"is this daemon still alive?"</i> probe;</item>
///   <item>collect lightweight host-level telemetry (CPU%, memory used) without
///         a separate metrics pipeline.</item>
/// </list>
///
/// <para><b>Direction matters:</b> this lives in <c>RuntimeServerPayloads</c> —
/// not <c>RuntimeClientPayloads</c> — because the daemon sends it <i>to</i> the
/// hub. Server-pushed daemon notifications (StartTurn, CancelTurn, …) live in
/// <c>RuntimeClientPayloads</c>. Keeping the two directions in different files
/// avoids the constant "wait, who sends what?" confusion when scanning the
/// generated TypeScript.</para>
///
/// <para><see cref="EmittedAt"/> is the daemon's wall clock at the moment of
/// emit. It is preserved on the wire for telemetry / clock-skew analysis but
/// the server intentionally <b>does not</b> use it to update
/// <c>LastHeartbeatAt</c> — the source of truth for "did this beat actually
/// arrive at the server" is the server's own clock at receive time. A daemon
/// with a wildly skewed clock cannot fool the staleness detector.</para>
///
/// <para><see cref="DaemonVersion"/> is required (the daemon always knows its
/// own version). <see cref="CpuPercent"/> and <see cref="MemoryUsedMb"/> are
/// nullable because gathering host metrics may fail or be disabled; we still
/// want the heartbeat itself to land in that case.</para>
///
/// <para><b>Health-snapshot fields (Phase D).</b>
/// <see cref="DiskUsedPct"/> mirrors <c>DiskMonitor.latest()</c>'s used fraction
/// expressed as 0..100; <c>null</c> when the daemon hasn't sampled yet.
/// <see cref="SupervisedServicesUp"/> is the list of services <i>currently</i>
/// reported as RUNNING by supervisord — empty list means "no services are
/// supervised" (and the backend's service-down detector treats <c>null</c> as
/// "this daemon doesn't yet report services" so we don't false-alarm older
/// daemons). <see cref="ActiveSessionId"/> is the session the TurnRunner is
/// running right now, <c>null</c> when idle. All three are nullable for the
/// same reason CpuPercent / MemoryUsedMb are: the heartbeat itself must land
/// even if a contributor has nothing to report.</para>
/// </summary>
[TranspilationSource]
public record HeartbeatPayload(
    DateTime EmittedAt,
    string DaemonVersion,
    double? CpuPercent,
    long? MemoryUsedMb,
    double? DiskUsedPct = null,
    IReadOnlyList<string>? SupervisedServicesUp = null,
    Guid? ActiveSessionId = null,
    /// <summary>
    /// runtime-observability-super-admin — latest <c>DiskMonitor</c> sample at
    /// heartbeat-emit time. Nullable: daemon hasn't sampled yet, or the disk
    /// path was unreachable on this tick. <see cref="DiskSample.SampledAt"/>
    /// is the daemon's wall clock; the server stamps its own
    /// <c>LastDiskSampledAt</c> only when this field is non-null.
    /// </summary>
    DiskSamplePayload? Disk = null,
    /// <summary>
    /// runtime-observability-super-admin — JSON-serialised
    /// <c>SysstatsSnapshot</c> from <c>ProcessStatsCollector</c>: top-N
    /// process RSS / CPU% plus aggregate network rx/tx rates. Carried as
    /// already-serialized JSON (same convention as
    /// <see cref="EmitEventPayload.EventData"/>) so the daemon doesn't have
    /// to marshal a complex discriminated structure on every heartbeat.
    /// Persisted verbatim to <c>ProjectRuntime.LastSysstatsSnapshot</c>; the
    /// drawer re-parses on the way in.
    /// </summary>
    string? SysstatsSnapshotJson = null);

/// <summary>
/// Embedded disk-sample snapshot carried inside <see cref="HeartbeatPayload"/>.
/// Mirrors the daemon's <c>DiskMonitor.latest()</c> shape — used + total bytes
/// plus the daemon's wall-clock at the moment <c>statfs</c> ran. Distinct from
/// <see cref="DiskPressurePayload"/>: that one is the rare on-transition push
/// and includes a level string; this is the periodic heartbeat snapshot the
/// drawer renders as a live gauge.
/// </summary>
[TranspilationSource]
public record DiskSamplePayload(
    long UsedBytes,
    long TotalBytes,
    DateTime SampledAt);

/// <summary>
/// Daemon-to-server agent event push. Every <see cref="AgentEvent"/> the daemon
/// produces while running an <see cref="AgentSession"/> arrives over this
/// payload via <c>RuntimeHub.EmitEvent</c>. The hub assigns the monotonic
/// per-session sequence server-side, persists the row, advances session +
/// conversation state, and raises an <c>AgentEventEmitted</c> domain event for
/// the broadcast handler to fan out to the chat panel.
///
/// <list type="bullet">
///   <item><see cref="SessionId"/> identifies which <see cref="AgentSession"/>
///         this event belongs to. The hub looks up the session, its parent
///         <c>Conversation</c>, and the owning project from this Guid.
///         <b>Nullable</b>: the daemon also reuses <c>EmitEvent</c> as a
///         best-effort runtime-scope channel for bootstrap / shutdown
///         progress events that genuinely have no session yet. Those calls
///         arrive with a <c>null</c> (or empty-string-coerced-to-null, via
///         <c>EmptyStringNullableGuidJsonConverter</c> on the SignalR JSON
///         protocol) sessionId and the hub short-circuits before touching
///         the session/conversation tables. The wire-side empty-string
///         sentinel is what older daemons emit; new daemons should send
///         <c>null</c> outright.</item>
///   <item><see cref="Kind"/> is the closed enum (Cursor SDK message kind) the
///         audit trail / chat panel speak — server stores it as a string.</item>
///   <item><see cref="EventData"/> is <b>already-serialized JSON</b> from the
///         daemon — the legacy carrier kept for non-Cursor envelopes
///         (bootstrap progress, runtime_ready ping). Cursor-native chat events
///         populate the first-class fields below instead and leave
///         <see cref="EventData"/> as <c>"{}"</c>.</item>
///   <item><b>First-class typed fields (cursor-native-chat-ux card 3).</b>
///         Each per-kind cluster (<see cref="Text"/>,
///         <see cref="ThinkingDurationMs"/>, <see cref="ToolCallId"/> ...) is
///         nullable so a single payload only carries the columns that apply
///         to its <see cref="Kind"/>. The hub writes these straight to the
///         per-kind nullable columns on <see cref="AgentEvent"/> without
///         parsing JSON. <see cref="RunResult"/> rides along with the
///         terminal Status payload — when populated, the hub upserts a
///         <c>RunResults</c> row for the session.</item>
///   <item><see cref="EmittedAt"/> is the daemon's clock at emit time; preserved
///         on the wire for telemetry / clock-skew analysis. The server stamps
///         <c>AgentEvent.CreatedAt</c> with its own <c>UtcNow</c> at receive —
///         daemon clocks cannot influence ordering.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record EmitEventPayload(
    Guid? SessionId,
    AgentEventKind Kind,
    string EventData,
    DateTime EmittedAt,
    // ----- Text-bearing kinds (PromptReceived, AssistantText, Thinking) ----
    string? Text = null,
    long? ThinkingDurationMs = null,
    // ----- ToolUse cluster (SDKToolUseMessage) -----
    string? ToolCallId = null,
    string? ToolName = null,
    AgentEventToolStatus? ToolStatus = null,
    /// <summary>Tool args as already-serialized JSON; stored verbatim in the jsonb column.</summary>
    string? ToolArgs = null,
    /// <summary>Tool result as already-serialized JSON; stored verbatim in the jsonb column.</summary>
    string? ToolResult = null,
    bool? ToolArgsTruncated = null,
    bool? ToolResultTruncated = null,
    // ----- Status cluster (SDKStatusMessage) -----
    AgentEventRunStatus? RunStatus = null,
    string? StatusMessage = null,
    // ----- Task cluster (SDKTaskMessage) -----
    string? TaskId = null,
    string? TaskTitle = null,
    // ----- Terminal run-result aggregate (rides along with terminal Status) -----
    RunResultPayload? RunResult = null);

/// <summary>
/// Per-turn aggregate result the daemon ships piggybacked on the terminal
/// <see cref="AgentEventKind.Status"/> emit. Mirrors Cursor SDK's
/// <c>RunResult</c> + <c>agent.listArtifacts()</c> shapes one-to-one — the
/// hub upserts a <c>RunResults</c> row keyed by session id with the
/// <see cref="Artifacts"/> array serialized into the jsonb column.
///
/// <para><see cref="DurationMs"/> and <see cref="Model"/> always present; the
/// git pair (<see cref="GitBranch"/>, <see cref="GitPrUrl"/>) only when the
/// agent actually touched git. <see cref="Artifacts"/> is always materialized
/// (possibly empty) so the chat panel's "N files edited" chip can just
/// <c>.length</c>.</para>
/// </summary>
[TranspilationSource]
public record RunResultPayload(
    long DurationMs,
    string Model,
    string? GitBranch,
    string? GitPrUrl,
    IReadOnlyList<RunArtifactPayload> Artifacts);

/// <summary>
/// One artifact entry inside <see cref="RunResultPayload"/>. Mirrors Cursor
/// SDK's <c>SDKArtifact</c> shape — path, size, mtime.
/// </summary>
[TranspilationSource]
public record RunArtifactPayload(
    string Path,
    long SizeBytes,
    DateTime UpdatedAt);

/// <summary>
/// Daemon-to-server error report. Routed via
/// <see cref="Hubs.RuntimeHub.ReportError"/> and persisted as a
/// <c>RuntimeErrorReport</c> row for operator triage. The daemon decides
/// what's worth reporting — boot failures, hook crashes, sandbox refusals,
/// out-of-disk, malformed config files. State-machine implications are
/// out-of-band: a runtime can keep running after reporting an error; the
/// row is for diagnostics only.
///
/// <list type="bullet">
///   <item><see cref="Category"/> is a free-form short tag the daemon picks
///         (<c>"hook"</c>, <c>"sandbox"</c>, <c>"bootstrap"</c>, …). Capped
///         at 64 chars; we store as a string so adding categories never
///         requires a coordinated server deploy.</item>
///   <item><see cref="Message"/> is a one-line human-readable summary
///         intended for the admin error feed. Capped server-side to 4000
///         chars; longer payloads belong in <see cref="StackTrace"/> /
///         <see cref="Context"/>.</item>
///   <item><see cref="StackTrace"/> is optional. When present, it carries
///         the daemon-side stack — node, python, whatever. Capped at 16000
///         chars on persist; older daemons / non-language errors leave it null.</item>
///   <item><see cref="Context"/> is an optional opaque diagnostic blob the
///         daemon supplies — typically JSON, but we treat it as a string and
///         don't validate. Capped at 16000 chars. Use cases: which hook
///         fired, the offending env var, a redacted command line.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record ErrorReportPayload(
    string Category,
    string Message,
    string? StackTrace,
    string? Context);

/// <summary>
/// Daemon-to-server disk-pressure transition (Phase D Card 3). Pushed by
/// <c>DiskMonitor.on('pressure', ...)</c> on the daemon — the monitor only
/// emits on level transitions (ok→warn, warn→critical, critical→warn, …) so
/// this payload is rare relative to <see cref="HeartbeatPayload"/>: at most a
/// handful per runtime per day under normal operations.
///
/// <para>Routed via <c>RuntimeHub.ReportDiskPressure</c> and persisted as
/// a <c>RuntimeDiskPressureEvent</c> row for the operator timeline. The hub
/// also fans the event out to <c>project-{ProjectId}</c> SignalR clients via
/// <c>IAgentClient.RuntimeDiskPressure</c> so the project dashboard can
/// surface the warning without polling.</para>
///
/// <list type="bullet">
///   <item><see cref="Level"/> mirrors the daemon's <c>DiskPressureLevel</c>
///         enum — <c>"ok"</c>, <c>"warn"</c>, <c>"critical"</c>. Capped at 16
///         chars; stored as string so the vocabulary can grow without a wire
///         break.</item>
///   <item><see cref="UsedBytes"/> + <see cref="TotalBytes"/> are the
///         <c>statfs</c> sample at transition time. Numbers (not bigints) — a
///         runtime's disk capacity in bytes fits in <c>long</c>.</item>
///   <item><see cref="UsedPct"/> is the daemon-computed used fraction expressed
///         as 0..100. Denormalised so the persistence + broadcast paths don't
///         re-divide.</item>
///   <item><see cref="SampledAt"/> is the daemon's wall clock at sample time;
///         preserved on the wire for clock-skew analysis. The server stamps
///         <c>RuntimeDiskPressureEvent.ReportedAt</c> with its own UtcNow at
///         receive — daemon clock skew cannot shuffle the timeline.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record DiskPressurePayload(
    string Level,
    long UsedBytes,
    long TotalBytes,
    double UsedPct,
    DateTime SampledAt);

/// <summary>
/// Daemon-to-server per-message cost report (Claude path). Emitted by the
/// daemon's <c>onRawMessage</c> callback once per assistant message that
/// carries a <c>usage</c> block — so an N-message turn produces N calls,
/// each carrying the incremental tokens for that single message and a
/// derived <see cref="TotalCostUsd"/> computed from the model's price card.
///
/// <para><b>Accumulation semantics.</b> The hub adds each report to the
/// running totals on <c>AgentSession</c>:
/// <c>TotalCostUsd += payload.TotalCostUsd</c>, same for every token field.
/// The daemon does not deduplicate by message id; if it retries a call the
/// session will double-count. This is a deliberate trade — the daemon
/// resends only on transient socket errors which are vanishingly rare
/// relative to the per-message volume, and the worst-case over-report is
/// bounded to the cost of one extra message.</para>
///
/// <para><b>Why not piggy-back on EmitEvent?</b> The legacy daemon's
/// <c>onRawMessage</c> path runs on the streaming hot path — before any
/// <c>TurnCompleted</c> envelope exists — and bundles per-message usage
/// directly. Folding cost into the result frame would require either
/// buffering per-message usage on the daemon (the deployed binary doesn't)
/// or recomputing on the server (no per-model price card here). A
/// dedicated channel keeps the deployed daemon and the server in lockstep
/// without retrofitting either.</para>
///
/// <para>The newer <c>/workspace/packages/daemon</c> path will fold cost
/// into the <c>TurnCompleted</c> event itself (<see cref="EmitEventPayload"/>)
/// — its handler (<c>RecordSessionCostHandler</c>) parses the JSONB and
/// stamps the session row idempotently. Both paths coexist until the new
/// daemon replaces the deployed one.</para>
/// </summary>
[TranspilationSource]
public record ReportSessionCostPayload(
    decimal TotalCostUsd,
    int InputTokens,
    int OutputTokens,
    int CacheCreationTokens,
    int CacheReadTokens);

/// <summary>
/// Response shape for <c>RuntimeHub.GetSecrets()</c> — the daemon's BYOK
/// fetch hook. Carries the two Anthropic credentials the runtime needs to
/// talk to Claude on the user's behalf, with each independently nullable so
/// the daemon can react to "no Anthropic key configured anywhere on this
/// stack" by surfacing an actionable error to the user instead of silently
/// failing inside the SDK.
///
/// <para><b>Direction.</b> Server → daemon (returned synchronously from a
/// daemon-invoked hub method). Lives in <c>RuntimeServerPayloads</c> with
/// the other daemon-side wire shapes — same file as
/// <see cref="HeartbeatPayload"/> and friends, so the generated TypeScript
/// surface keeps both halves of the BYOK fetch (request → response) in one
/// place.</para>
///
/// <para><b>Resolution chain</b> (mirrored on the server in
/// <c>RuntimeHub.GetSecrets</c>):
/// <list type="number">
///   <item>Per-project encrypted column on the <c>Project</c> row.</item>
///   <item>Process-level environment variable
///         (<c>ANTHROPIC_API_KEY</c> / <c>CLAUDE_CODE_OAUTH_TOKEN</c>).</item>
/// </list>
/// Whichever step first yields a non-empty value wins; both fields can be
/// independently <c>null</c> if no source supplied that credential.</para>
///
/// <para><b>Logging hygiene.</b> Server-side logging stamps boolean
/// presence flags only; the values themselves never reach a logger or
/// trace. The daemon side mirrors this rule: the values are passed straight
/// into the SDK and not echoed.</para>
///
/// <para><b>OpenCode thin-slice extension.</b>
/// <see cref="OpencodeZenApiKey"/> is the third independently-nullable slot,
/// resolved via the same per-project envelope → SystemSettings
/// (<c>OPENCODE_ZEN_API_KEY</c>) → env-var (<c>OPENCODE_ZEN_API_KEY</c>)
/// fallback chain. Carried alongside the Claude credentials so an
/// OpenCode-backed project can authenticate without a parallel hub method.
/// Old daemons reading this DTO ignore the field; new daemons hand it to
/// <c>OpencodeFactory</c> when their session's snapshot says
/// <c>agentBackend === "opencode"</c>. The field is nullable for the
/// "no key configured anywhere" case — the daemon surfaces an actionable
/// error rather than silently failing inside the SDK.</para>
///
/// <para><b>Cursor backend extension.</b>
/// <see cref="CursorApiKey"/> is the fourth independently-nullable slot,
/// resolved via the per-project encrypted envelope
/// (<c>Project.EncryptedCursorApiKey</c>) → host env-var
/// (<c>CURSOR_API_KEY</c>) fallback chain. No SystemSettings tier today —
/// the Cursor SDK key is BYOK-only at the project scope, mirroring the
/// pre-OpenCode-thin-slice Anthropic resolution shape. New daemons hand it
/// to the Cursor SDK factory when their session snapshot's
/// <c>agentBackend === "cursor"</c>; old daemons ignore the field. Nullable
/// for the "no key configured anywhere" case so the daemon can surface an
/// actionable error rather than silently failing inside the SDK.</para>
/// </summary>
[TranspilationSource]
public record AgentSecretsDto(string? CursorApiKey);

/// <summary>
/// Daemon-to-server runtime event push. Pushed by the daemon's
/// <c>RuntimeEventEmitter</c> for every structured event in the V2 taxonomy
/// (bootstrap stages, install snippets, setup commands, supervised service
/// lifecycle, spec delta apply / fail — see <c>RuntimeEventTypes</c>).
/// Routed via <c>RuntimeHub.RecordRuntimeEvent</c>; the hub resolves the
/// owning <c>RuntimeId</c> from the connection's signed <c>rt_runtime</c>
/// claim (the daemon does not — and cannot — supply it on the wire) and
/// dispatches a <c>RecordRuntimeEventCommand</c> to persist the row. After
/// the row lands, the hub fans the payload out to the
/// <c>runtime-events:{runtimeId}</c> SignalR group so frontends subscribed
/// to that runtime's drawer receive the live event.
///
/// <list type="bullet">
///   <item><see cref="Type"/> is one of the <c>RuntimeEventTypes</c> string
///         constants. Stored as varchar on the server — the daemon can emit
///         a new type without a coordinated server deploy.</item>
///   <item><see cref="Severity"/> mirrors <c>RuntimeEventSeverity</c>
///         (<c>"Info"</c> / <c>"Warn"</c> / <c>"Error"</c>). The hub parses
///         this back into the enum before dispatch.</item>
///   <item><see cref="Timestamp"/> is the daemon's UTC clock at emit time —
///         the source of truth for "when did this happen?". The drawer's
///         Timeline orders on this, not the server's stamp-at-insert clock,
///         so batched events still render in domain order.</item>
///   <item><see cref="DurationMs"/> is populated on <c>*Completed</c> /
///         <c>*Failed</c> / <c>*Skipped</c> events (paired with a prior
///         <c>*Started</c>) and null on bare <c>*Started</c> events or
///         one-shot events with no natural pairing.</item>
///   <item><see cref="Payload"/> is already-serialized JSON the daemon
///         supplies — same convention as <c>EmitEventPayload.EventData</c>.
///         Stored verbatim in the persistence layer's <c>jsonb</c> column;
///         the frontend re-parses on the way in. We use <c>string</c> on
///         the wire (not <c>JsonElement</c>) because Tapper's TS codegen
///         can't transpile <c>JsonElement</c> directly — see the same
///         comment on <c>PermissionRequestedPayload.ToolInput</c>.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record RuntimeEventPayloadDto(
    string Type,
    string Severity,
    DateTime Timestamp,
    long? DurationMs,
    string Payload);

/// <summary>
/// Daemon-to-server spec-health report (self-healing-runtime-specs, card B1).
/// Pushed once by the daemon's bootstrap orchestrator right after the spec
/// stages run — and again whenever a live spec-delta apply changes the health
/// — via <c>RuntimeHub.ReportSpecHealth</c>. The hub resolves the owning
/// <c>RuntimeId</c> from the connection's signed <c>rt_runtime</c> claim — the
/// daemon does not (and cannot) supply it on the wire — maps
/// <see cref="Health"/> to the <c>RuntimeSpecHealth</c> enum, and persists it
/// to <c>ProjectRuntime.SpecHealth</c>. Best-effort: the hub never throws, so a
/// persistence blip only delays the banner, it doesn't break a working runtime.
///
/// <list type="bullet">
///   <item><see cref="Health"/> is one of <c>"Healthy"</c> / <c>"Degraded"</c> /
///         <c>"Unknown"</c> — mirrors <c>RuntimeSpecHealth</c>. Stored as a
///         string on the runtime row; an unrecognised value maps to
///         <c>Unknown</c> + a warn log so a daemon that adds a health level
///         ahead of the server doesn't drop the report.</item>
///   <item><see cref="Summary"/> is an optional one-line human gloss
///         (e.g. "2 services failed to start") the banner can render without
///         re-deriving it from the issue list. Null when the daemon has nothing
///         to add.</item>
///   <item><see cref="Issues"/> is the daemon's in-memory <c>BootIssue[]</c>
///         carried as already-serialized JSON strings (same convention as
///         <see cref="RuntimeEventPayloadDto.Payload"/> / <c>EventData</c> — we
///         use <c>string</c> not <c>JsonElement</c> because Tapper's TS codegen
///         can't transpile <c>JsonElement</c>). The hub does NOT persist these
///         here — boot-issue <i>details</i> live in <c>RuntimeEvents</c> /
///         <c>RuntimeErrorReports</c>, emitted separately as
///         <c>SpecDegraded</c> events. The array rides along purely so a future
///         consumer (or logging) has the structured payload without a re-read.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record ReportSpecHealthPayload(
    string Health,
    string? Summary,
    IReadOnlyList<string> Issues);

/// <summary>
/// Daemon-to-server log-line push. The daemon's <c>LogTailer</c> emits one of
/// these for every line it reads from <c>/var/log/supervisor/{serviceName}.log</c>
/// while a tail is active. The hub resolves the owning <c>RuntimeId</c> from
/// the connection's signed <c>rt_runtime</c> claim — the daemon does not (and
/// cannot) supply it on the wire — then broadcasts to the
/// <c>service-logs:{runtimeId}:{serviceName}</c> group via
/// <see cref="Hubs.IAgentClient.ServiceLogLine"/>.
///
/// <para><b>Not persisted.</b> The hub broadcasts and discards — log lines do
/// not land in the event store. The disk file (rotated by supervisord) is the
/// only durable source.</para>
///
/// <para><see cref="Timestamp"/> is the daemon's wall-clock at the moment the
/// line was read; supervisord does not stamp lines, so this is the closest
/// approximation we have. <see cref="Line"/> is a single line with no trailing
/// newline (the daemon strips it).</para>
/// </summary>
[TranspilationSource]
public record ServiceLogLineDto(
    string ServiceName,
    string Line,
    DateTime Timestamp);

/// <summary>
/// runtime-observability-super-admin — daemon-to-server log-line push from
/// the daemon's own stdout/stderr. The daemon's <c>LogTailer</c> emits one of
/// these for every line read from
/// <c>/var/log/supervisor/agent.out.log</c> + <c>/var/log/supervisor/agent.err.log</c>
/// while a daemon-log tail is active. The hub resolves the owning
/// <c>RuntimeId</c> from the connection's signed <c>rt_runtime</c> claim,
/// then broadcasts to the <c>daemon-logs:{runtimeId}</c> group via
/// <see cref="Hubs.IAgentClient.DaemonLogLineReceived"/>.
///
/// <para><b>Not persisted.</b> The hub broadcasts and discards — the supervisord-rotated
/// log file on disk is the only durable copy.</para>
///
/// <para><see cref="Stream"/> is <c>"stdout"</c> or <c>"stderr"</c> so the
/// drawer can colour-code the rows. Stored as string so adding a new stream
/// kind never requires a coordinated server deploy.</para>
/// </summary>
[TranspilationSource]
public record DaemonLogLineDto(
    string Stream,
    string Line,
    DateTime Timestamp);

/// <summary>
/// One supervisord process entry inside a <see cref="LiveSupervisordSnapshotPayload"/>.
/// Mirrors the daemon's <c>LiveSupervisordSnapshotProcess</c> shape and carries
/// just enough state for the super-admin runtime drawer's Services tab to render
/// FATAL / BACKOFF / STOPPED rows that the event-driven Timeline can't fully
/// represent on its own.
///
/// <para>Numeric / string fields are nullable where supervisord doesn't always
/// have a value to report: <see cref="Pid"/> is 0 / null on a never-running
/// program; <see cref="UptimeMs"/> is null when not in RUNNING; <see cref="ExitStatus"/>
/// is null when the process has never exited; <see cref="SpawnErr"/> is null when
/// no spawn error was captured; <see cref="StartedAt"/> is null when never started.</para>
/// </summary>
[TranspilationSource]
public record LiveSupervisordSnapshotProcess(
    string Name,
    string State,
    int Pid,
    long? UptimeMs,
    int? ExitStatus,
    string? SpawnErr,
    DateTime? StartedAt);

/// <summary>
/// Daemon-to-server push of the live supervisord process snapshot. Pushed by
/// the daemon's <c>ServiceStatusPoller</c> on every poll tick (default 10s) so
/// the super-admin runtime drawer's Services tab has an always-fresh view of
/// what supervisord thinks is going on — including transient states (BACKOFF,
/// STOPPED, FATAL) the event-driven Timeline can't represent alone.
///
/// <para><b>Not persisted.</b> The hub fans this out to the
/// <c>runtime-events:{runtimeId}</c> group via
/// <see cref="Hubs.IAgentClient.LiveSupervisordSnapshotReceived"/> and discards.
/// Consumers (the drawer's Services tab) cache the latest snapshot keyed by
/// runtimeId and replace on every push.</para>
///
/// <para><see cref="SampledAt"/> is the daemon's UTC clock at the moment of the
/// snapshot; the wire-side clock for ordering purposes. <see cref="Processes"/>
/// is the full set of processes supervisord knew about at sample time — empty
/// list means supervisord returned nothing (rare; usually means the daemon hit
/// a transport error this tick and pushed an empty snapshot to signal liveness).</para>
/// </summary>
[TranspilationSource]
public record LiveSupervisordSnapshotPayload(
    DateTime SampledAt,
    IReadOnlyList<LiveSupervisordSnapshotProcess> Processes);
