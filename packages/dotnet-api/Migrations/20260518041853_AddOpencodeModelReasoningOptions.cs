using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Adds the <c>OpencodeModels.ReasoningOptions</c> jsonb column and backfills
    /// per-provider reasoning configuration on the curated catalog (the
    /// b0000000-... namespace inserted by <c>ReplaceOpencodeModelsSeed</c>).
    ///
    /// <para><b>Why this exists.</b> Per the opencode-server v1.15 SDK, reasoning
    /// (Anthropic <c>thinking</c>, OpenAI Responses <c>reasoningSummary</c>) only
    /// streams as a <c>ReasoningPart</c> when the per-model <c>options</c> bag
    /// asks for it. Without these options, the model still <i>thinks</i>
    /// server-side (and burns reasoning tokens for OpenAI) but the deltas arrive
    /// as plain <c>TextPart</c>s — which our daemon mapper then surfaces as
    /// <c>AssistantText</c> instead of <c>AssistantThinking</c>. The result was
    /// "thinking content rendered as the answer" across every opencode session
    /// we inspected. See <c>BootstrapOpencodeStage</c> for the daemon-side
    /// write-through into <c>provider.opencode.models.{slug}.options</c>.</para>
    ///
    /// <para><b>Per-family defaults.</b>
    /// <list type="bullet">
    ///   <item><b>Claude</b> (claude-*): extended-thinking enabled with a 16k
    ///         budget — high enough to surface substantive reasoning on real
    ///         engineering questions without being so high it dominates the
    ///         output token budget. Users can override per-project later when
    ///         we ship the admin CRUD slice.</item>
    ///   <item><b>GPT-5</b> family (gpt-5*): <c>reasoningEffort: high</c>,
    ///         <c>reasoningSummary: auto</c>, <c>textVerbosity: low</c>. The
    ///         <c>auto</c> summary mode is the critical bit — without it
    ///         the Responses API thinks silently and emits nothing for the
    ///         UI to render. Verbosity=low keeps the answer concise so the
    ///         thinking block isn't dwarfed.</item>
    ///   <item><b>Everything else</b> (Gemini, DeepSeek, Kimi, GLM, Qwen,
    ///         MiniMax, Nemotron, Ring, Trinity, big-pickle): null. These
    ///         either don't support visible reasoning, or surface it via a
    ///         different mechanism (Kimi/DeepSeek <c>reasoning_content</c>
    ///         via <c>interleaved.field</c>) that the upstream opencode catalog
    ///         doesn't expose to a per-model options override. Tracked as a
    ///         separate upstream gap.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Idempotent.</b> Re-running on an already-patched database is a
    /// no-op — every <c>UPDATE</c> filters on the matching slug and the
    /// <c>ReasoningOptions</c> column is overwritten with the same value.
    /// Operator-set custom options on these rows would be clobbered, but we
    /// don't expose admin CRUD yet so that's not a current concern.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddOpencodeModelReasoningOptions : Migration
    {
        // jsonb literals — kept as constants so the SQL below stays readable.
        private const string ClaudeThinkingOptions =
            "{\"thinking\":{\"type\":\"enabled\",\"budgetTokens\":16000}}";

        private const string Gpt5ReasoningOptions =
            "{\"reasoningEffort\":\"high\",\"reasoningSummary\":\"auto\",\"textVerbosity\":\"low\"}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the column. Nullable jsonb — null means "no special options",
            // the daemon writes the plain api-key shape it always wrote.
            migrationBuilder.AddColumn<string>(
                name: "ReasoningOptions",
                table: "OpencodeModels",
                type: "jsonb",
                nullable: true);

            // Claude family — extended thinking with a 16k budget. Matches all
            // claude-* slugs in the curated catalog (haiku/opus/sonnet variants).
            // Filter on slug rather than Id so the migration survives a future
            // catalog reseed that keeps the same slugs under different Guids.
            migrationBuilder.Sql(
                $$"""
                UPDATE "OpencodeModels"
                SET "ReasoningOptions" = '{{ClaudeThinkingOptions}}'::jsonb
                WHERE "Slug" LIKE 'claude-%'
                  AND "IsDeleted" = false;
                """);

            // GPT-5 family — Responses API reasoning summary stream. Matches
            // every gpt-5* slug in the curated catalog (base + codex + nano +
            // mini + pro + version-bumped variants).
            migrationBuilder.Sql(
                $$"""
                UPDATE "OpencodeModels"
                SET "ReasoningOptions" = '{{Gpt5ReasoningOptions}}'::jsonb
                WHERE "Slug" LIKE 'gpt-5%'
                  AND "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the column drops the data with it — no need to NULL
            // first. Down is intentionally non-destructive of other columns.
            migrationBuilder.DropColumn(
                name: "ReasoningOptions",
                table: "OpencodeModels");
        }
    }
}
