using Source.Features.Conversations.Models;
using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// Payload pushed to React clients whenever a <c>ProjectRuntime</c>
/// transitions states. Mirrors the fields of <c>RuntimeStateChanged</c> so the
/// frontend can update its local lifecycle view without a round-trip to the API.
///
/// <para>State values are serialised as their enum names ("Online", "Booting", …)
/// rather than ordinal integers — wire stability across enum reorderings, and
/// JS-friendly switch statements on the client.</para>
///
/// <para><see cref="Reason"/> is the structured machine code
/// (e.g. <c>"provisioner:no_active_image"</c>) and <see cref="ErrorMessage"/> is
/// the human-readable metadata that accompanied the transition. Both are populated
/// for every transition; the frontend typically only renders <c>ErrorMessage</c>
/// when <see cref="ToState"/> is <c>"Failed"</c>.</para>
/// </summary>
[TranspilationSource]
public record RuntimeStateChangedNotification(
    Guid RuntimeId,
    Guid ProjectId,
    string? FromState,
    string ToState,
    string Reason,
    DateTime ChangedAt,
    string? ErrorMessage = null);

/// <summary>
/// Coarse-grained boot progress events streamed to the React client while the
/// daemon is running through the bootstrap state graph (cloning, installing
/// deps, running hooks, ready). The full stage vocabulary lands with the
/// runtime-bootstrap spec; sketched here for the typed-hub contract only.
/// </summary>
[TranspilationSource]
public record BootstrapProgressNotification(
    Guid RuntimeId,
    string Stage,
    int Progress);

/// <summary>
/// Pushed to the <c>project-{projectId}</c> group the moment a wake-on-connect
/// flow flips a Suspended runtime back toward Online. The
/// <c>RuntimeStateChanged</c> notification tells the same story but lags by the
/// Fly machine spin-up + first heartbeat — clients want a "we're waking up,
/// hold on" affordance immediately, before the actual state transition lands.
///
/// <para><see cref="RuntimeId"/> is included so a client tracking multiple
/// projects can correlate. <see cref="ProjectId"/> is redundant with the group
/// scope but kept on the payload for symmetry with <see cref="RuntimeStateChangedNotification"/>.</para>
/// </summary>
[TranspilationSource]
public record RuntimeWakingNotification(
    Guid RuntimeId,
    Guid ProjectId);

/// <summary>
/// Generic in-app notification surface — toast / banner messages pushed from
/// the platform. Severity drives styling on the client. Concrete callers
/// (e.g. quota warnings, runtime errors) ship in dependent features.
/// </summary>
[TranspilationSource]
public record NotificationPayload(
    string Title,
    string Message,
    string Severity);

/// <summary>
/// Payload pushed to React clients for every <c>AgentEvent</c> the daemon emits
/// into an <c>AgentSession</c>. Carries the routing scope
/// (<see cref="ConversationId"/> / <see cref="ProjectId"/> /
/// <see cref="BranchId"/>) plus the fully-projected polymorphic
/// <see cref="AgentEventDto"/> snapshot so the client can drop the event
/// straight into its in-memory conversation log without a round-trip.
///
/// <para><b>Card 3 (cursor-native-chat-ux).</b> The pre-rewrite version
/// shipped only minimal "row N of kind K landed" prods and required the
/// frontend to refetch the row body via REST. The Cursor-native schema's
/// first-class columns let us ship the full row inline — typical chat-panel
/// events are sub-kilobyte and the round-trip elimination is worth the wire
/// bytes.</para>
///
/// <para><see cref="Event"/> is the discriminated union (<c>eventKind</c>
/// property names the concrete subtype on the wire — see
/// <see cref="AgentEventDto"/>). The React client switches on
/// <c>event.eventKind</c> and renders the appropriate row component.</para>
/// </summary>
[TranspilationSource]
public record AgentEventNotification(
    Guid ConversationId,
    Guid ProjectId,
    Guid BranchId,
    AgentEventDto Event);

/// <summary>
/// Pushed to the <c>branch-{branchId}</c> and <c>workspace-{workspaceId}</c>
/// groups whenever a session's terminal <see cref="AgentEventKind.Status"/>
/// frame lands and a <see cref="RunResultDto"/> row was upserted. Lets the
/// chat panel render the per-turn footer
/// (<i>"Finished in 14.2s · claude-sonnet-4 · 5 files edited · view PR ↗"</i>)
/// without a REST round-trip.
///
/// <para>Carries the routing scope alongside the <see cref="Result"/> snapshot
/// — same shape as <see cref="AgentEventNotification"/>. Only emitted when
/// the daemon actually shipped a run-result aggregate; status transitions
/// without one (e.g. Creating → Running) leave this channel quiet.</para>
/// </summary>
[TranspilationSource]
public record RunResultNotification(
    Guid ConversationId,
    Guid ProjectId,
    Guid BranchId,
    RunResultDto Result);

/// <summary>
/// Pushed to the <c>project-{ProjectId}</c> group whenever a conversation's
/// title changes — both the user-driven rename path (POST
/// <c>/api/conversations/{id}/rename</c>) and the one-shot auto-retitle
/// heuristic that fires off the first <c>AssistantText</c> chunk. Lets every
/// other open tab / browser pick up the new title live without a manual
/// refresh.
///
/// <para>Mirrors the persisted <c>ConversationRenamed</c> domain event 1:1 —
/// kept as a separate wire type so DB column changes don't ripple into the
/// React contract.</para>
/// </summary>
[TranspilationSource]
public record ConversationRenamedNotification(
    Guid ConversationId,
    Guid ProjectId,
    string Title,
    bool IsAutoTitled,
    DateTime OccurredAt);

/// <summary>
/// Pushed to the <c>project-{ProjectId}</c> group when a runtime crosses a
/// disk-pressure threshold (Phase D Card 3). One notification per emitted
/// transition — the daemon's <c>DiskMonitor</c> only emits on level changes,
/// so the broadcast is rare under normal operations and a flapping disk
/// surfaces as a series of pressure-change banners on the project dashboard.
///
/// <para>Mirrors the persisted <c>RuntimeDiskPressureEvent</c> row but is the
/// wire surface — kept separate so DB column changes don't ripple into the
/// React contract. <see cref="ReportedAt"/> is the server clock at receive,
/// matching the persisted row's <c>ReportedAt</c>; <see cref="SampledAt"/>
/// preserves the daemon's wall clock for any clock-skew telemetry the UI
/// chooses to render.</para>
/// </summary>
[TranspilationSource]
public record RuntimeDiskPressureNotification(
    Guid RuntimeId,
    Guid ProjectId,
    string Level,
    long UsedBytes,
    long TotalBytes,
    double UsedPct,
    DateTime SampledAt,
    DateTime ReportedAt);

/// <summary>
/// Pushed to the <c>runtime-events:{RuntimeId}</c> group after the daemon
/// records a structured runtime event via <c>RuntimeHub.RecordRuntimeEvent</c>.
/// Drives the live tail of the runtime drawer's Timeline tab — the user sees
/// install / setup / service-lifecycle / spec-delta events appear in real
/// time without polling the REST endpoint.
///
/// <para><b>Group, not project.</b> The drawer is per-runtime, and a project
/// may have multiple historical runtimes; we route on the runtime id rather
/// than the project id so an open drawer for runtime <c>A</c> never sees
/// events from runtime <c>B</c>. Clients opt in via
/// <c>AgentHub.SubscribeToRuntimeEvents(runtimeId)</c> on drawer mount and
/// opt out via the symmetric <c>UnsubscribeFromRuntimeEvents</c>.</para>
///
/// <para>Mirrors the persisted <c>RuntimeEvent</c> row 1:1 but is the wire
/// surface — kept separate so the persistence shape can evolve without a
/// generated-TypeScript break. <see cref="Payload"/> is the already-serialized
/// JSON the daemon supplied, passed through verbatim; the React side
/// <c>JSON.parse</c>s on receipt, same convention as the REST endpoint's
/// <c>RuntimeEventDto</c>.</para>
/// </summary>
[TranspilationSource]
public record RuntimeEventNotification(
    Guid Id,
    Guid RuntimeId,
    string Type,
    string Severity,
    DateTime Timestamp,
    long? DurationMs,
    string Payload);

/// <summary>
/// Pushed to the <c>service-logs:{RuntimeId}:{ServiceName}</c> group for every
/// stdout/stderr line the daemon tails out of
/// <c>/var/log/supervisor/{serviceName}.log</c>. Drives the live "Logs" tab in
/// the runtime drawer.
///
/// <para><b>Not persisted.</b> Unlike <see cref="RuntimeEventNotification"/>,
/// log lines are never written to the backend database — they live only on the
/// daemon's disk (rotated by supervisord) and are streamed to subscribers while
/// the Logs tab is open. Per the runtime-spec-v2 spec: "raw logs live on disk
/// and are tailed on demand."</para>
///
/// <para><b>Reference counting.</b> The daemon's <c>LogTailer</c> spawns one
/// <c>tail -F</c> process per <c>(runtimeId, serviceName)</c>, ref-counts
/// subscriptions, and tears the process down when the last subscriber leaves.
/// Backend subscribe/unsubscribe via <c>AgentHub.SubscribeToServiceLogs</c> /
/// <c>UnsubscribeFromServiceLogs</c>.</para>
///
/// <para><b>Timestamp</b> is the daemon's wall-clock at the moment the line
/// was emitted by the underlying process — supervisord doesn't stamp lines, so
/// the daemon stamps on read. Surface only — not durable.</para>
/// </summary>
[TranspilationSource]
public record ServiceLogLineNotification(
    Guid RuntimeId,
    string ServiceName,
    string Line,
    DateTime Timestamp);

/// <summary>
/// Pushed to the <c>runtime-events:{RuntimeId}</c> group every time the daemon's
/// <c>ServiceStatusPoller</c> takes a fresh supervisord snapshot (default every
/// 10 seconds). Drives the live "Services" tab in the super-admin runtime
/// drawer, which renders transient states (FATAL / BACKOFF / STOPPED) that the
/// event-driven Timeline can't fully represent on its own.
///
/// <para><b>Not persisted.</b> Pure push-through — the hub forwards the daemon's
/// payload to subscribers and discards. Consumers cache the latest snapshot
/// keyed by <see cref="RuntimeId"/> and replace on every push.</para>
///
/// <para><see cref="SampledAt"/> is the daemon's UTC clock at sample time;
/// <see cref="Processes"/> is the full set of processes supervisord reported.
/// An empty list means the daemon hit a transport error this tick — kept on
/// the wire as a liveness signal rather than suppressed.</para>
/// </summary>
[TranspilationSource]
public record LiveSupervisordSnapshotNotification(
    Guid RuntimeId,
    DateTime SampledAt,
    IReadOnlyList<LiveSupervisordSnapshotProcess> Processes);

/// <summary>
/// runtime-observability-super-admin — pushed to the <c>daemon-logs:{RuntimeId}</c>
/// group for every line the daemon tails out of
/// <c>/var/log/supervisor/agent.out.log</c> + <c>agent.err.log</c>. Drives the
/// super-admin runtime drawer's "Daemon Logs" tab. Not persisted (same as
/// <see cref="ServiceLogLineNotification"/>) — the supervisord-rotated log
/// file is the durable copy.
///
/// <para><see cref="Stream"/> is <c>"stdout"</c> or <c>"stderr"</c> so the
/// drawer can colour-code the rows. <see cref="Timestamp"/> is the daemon's
/// wall-clock at read time — supervisord doesn't stamp lines.</para>
/// </summary>
[TranspilationSource]
public record DaemonLogLineNotification(
    Guid RuntimeId,
    string Stream,
    string Line,
    DateTime Timestamp);

/// <summary>
/// Pushed to every <c>branch-{branchId}</c> group of a project AND the
/// parent <c>workspace-{workspaceId}</c> group when the project's preview
/// port (<c>Project.PreviewPort</c>) is hot-swapped via
/// <c>PATCH /api/projects/{projectId}/preview-port</c>. Lets every open tab —
/// project settings dialog, sidebar, runtime drawer — pick up the new port
/// live without a refetch.
///
/// <para>Emitted AFTER the DB write + Cloudflare ingress fan-out have
/// completed, so a client that receives this notification can trust the
/// new port is already live at the tunnel edge. <see cref="OccurredAt"/>
/// is the server's wall clock at notify time — useful for the UI to render
/// a "last changed at" affordance without polling.</para>
///
/// <para>Project-scoped, not branch-scoped: every branch of the project
/// runs the same dev-server port, so the broadcast carries only
/// <see cref="ProjectId"/> and is routed to every branch group of the
/// project on the server side.</para>
/// </summary>
[TranspilationSource]
public record PreviewPortChangedNotification(
    Guid ProjectId,
    int Port,
    DateTime OccurredAt);

/// <summary>
/// chat-file-attachments — pushed to the <c>branch-{branchId}</c> group when
/// an attachment's daemon-staging state changes (after the daemon acks via
/// <c>RuntimeHub.ReportAttachmentStaged</c>). The composer chip listens on
/// this and flips its UI between "uploading", "Ready", and "Failed".
///
/// <list type="bullet">
///   <item><see cref="AttachmentId"/> — the attachment row that changed.</item>
///   <item><see cref="ConversationId"/> — parent conversation.</item>
///   <item><see cref="BranchId"/> — embedded so a client tracking multiple
///         branches can correlate without re-deriving from the row.</item>
///   <item><see cref="State"/> — string state (<c>"Ready"</c> /
///         <c>"Failed"</c>); kept as a string for wire stability, matching
///         the other state-notification payloads.</item>
///   <item><see cref="Error"/> — optional human-readable error the daemon
///         reported; null on the success path.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record AttachmentStateChangedPayload(
    Guid AttachmentId,
    Guid ConversationId,
    Guid BranchId,
    string State,
    string? Error = null);
