using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Card 2 of <c>cursor-native-chat-ux</c>: wipe and recreate the AgentEvents
    /// table with the Cursor SDK's <c>SDKMessage</c>-shaped schema. The previous
    /// shape carried an opaque <c>EventData</c> JSONB blob keyed off a top-level
    /// Anthropic-shaped <c>EventType</c> enum (TurnStarted / TurnCompleted /
    /// TurnFailed / TurnCanceled / SystemMessage / ToolCall / AssistantText / ...).
    /// The Cursor SDK has a different vocabulary: <c>SDKAssistantTextMessage</c>,
    /// <c>SDKThinkingMessage</c>, <c>SDKToolUseMessage</c>, <c>SDKStatusMessage</c>,
    /// <c>SDKTaskMessage</c>, plus a per-turn <c>RunResult</c> aggregate. Rather
    /// than try to map one onto the other we DROP the table and recreate it under
    /// the new shape, with no data migration — the spec is explicit that "any
    /// in-flight chat history can be discarded; users are early-access and there
    /// is no production data to preserve."
    ///
    /// <para><b>Shape (single table + discriminator).</b> One row per emitted
    /// Cursor message. The <c>Kind</c> column says which subtype the row is, and
    /// only the per-kind nullable columns are populated. Cross-kind reads stay
    /// one-shot (chat panel does a single sequence-ordered scan over the table
    /// instead of joining N subtype tables), and the composite primary key
    /// <c>(SessionId, Sequence)</c> doubles as the clustered index for "give me
    /// events 100..200 of session X" range scans. See the entity comment on
    /// <c>AgentEvent</c> for the per-column rationale.</para>
    ///
    /// <para><b>RunResults table.</b> Per-turn aggregate (cost, duration, model,
    /// git branch / PR url, opaque artifacts JSON). 1:1 with <c>AgentSessions</c>
    /// via shared PK + cascade-delete; rewriting on a re-run is a delete+insert
    /// rather than an upsert because the row is small and the simpler semantics
    /// are worth the trivial extra write.</para>
    /// </summary>
    public partial class CursorNativeChatSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wipe the old AgentEvents table outright. The previous schema's
            // EventType/EventData columns map onto NOTHING in the Cursor-native
            // shape — a per-column rename + add would leave the table half in
            // each world. Drop + recreate is the clean cut the spec asked for.
            migrationBuilder.DropTable(name: "AgentEvents");

            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    // Composite PK part 1. FK + cascade to AgentSessions(Id).
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),

                    // Composite PK part 2. Per-session monotonic gap-free
                    // counter, server-assigned. bigint future-proofs against
                    // long-running sessions.
                    Sequence = table.Column<long>(type: "bigint", nullable: false),

                    // Discriminator. Stored as string for wire-stability across
                    // enum reorderings — same convention as ConversationStatus
                    // and RuntimeState. 32 chars is plenty for the longest
                    // enum-name (PromptReceived).
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),

                    // Server-stamped UTC clock at receive time. Not auto-set
                    // (the entity deliberately doesn't implement IAuditable —
                    // rows are immutable, so UpdatedAt has nothing to update).
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),

                    // ---- ToolUse columns (populated when Kind = ToolUse) ----
                    CallId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ToolStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Args = table.Column<string>(type: "jsonb", nullable: true),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    ArgsTruncated = table.Column<bool>(type: "boolean", nullable: true),
                    ResultTruncated = table.Column<bool>(type: "boolean", nullable: true),

                    // ---- Text-bearing column (AssistantText / Thinking /
                    //      PromptReceived all share Text; ThinkingDurationMs
                    //      is set only on the terminal thinking frame). ----
                    Text = table.Column<string>(type: "text", nullable: true),
                    ThinkingDurationMs = table.Column<long>(type: "bigint", nullable: true),

                    // ---- Status columns (populated when Kind = Status) ----
                    RunStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StatusMessage = table.Column<string>(type: "text", nullable: true),

                    // ---- Task columns (populated when Kind = Task) ----
                    TaskId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TaskTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => new { x.SessionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_AgentEvents_AgentSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Per-turn aggregate (cost / token usage / duration / model /
            // git branch + PR url / opaque per-tool artifacts). 1:1 with
            // AgentSession via shared PK + cascade-delete.
            migrationBuilder.CreateTable(
                name: "RunResults",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GitBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GitPrUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ArtifactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunResults", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_RunResults_AgentSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric tear-down. Note this drops ALL chat history — same
            // intentional wipe the Up path performs, just in the other
            // direction. No data is preserved on roll-back either.
            migrationBuilder.DropTable(name: "RunResults");
            migrationBuilder.DropTable(name: "AgentEvents");

            // Recreate the pre-Cursor shape so a roll-back leaves the table
            // looking exactly as it did before this migration. Schema only —
            // no data, by design.
            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventData = table.Column<string>(type: "jsonb", nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => new { x.SessionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_AgentEvents_AgentSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
