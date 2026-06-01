using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Data-only migration: try to enable reasoning streaming on Kimi, DeepSeek
    /// and GLM models by hinting opencode-server at the upstream-provider
    /// reasoning field name via <c>interleaved.field</c>.
    ///
    /// <para><b>Why a follow-up.</b> The first reasoning-options migration
    /// (<c>AddOpencodeModelReasoningOptions</c>) only seeded the providers we
    /// were sure about: Anthropic <c>thinking</c> for Claude and OpenAI
    /// <c>reasoningSummary</c> for GPT-5. Kimi K2.x, DeepSeek V4, and GLM 5.x
    /// surface reasoning via custom Chat-Completions fields
    /// (<c>reasoning_content</c> / <c>reasoning_details</c>) rather than a
    /// dedicated content block, so the SDK needs an explicit hint to know which
    /// field carries the thinking text.</para>
    ///
    /// <para><b>Best-effort, low blast radius.</b> The Zen-routed bundled
    /// <c>opencode/</c> provider may or may not forward the
    /// <c>interleaved.field</c> hint through to its upstream call — there's no
    /// documented contract either way and the failure mode is silent (server
    /// keeps returning plain text). If it works → free thinking blocks on
    /// these models. If it doesn't → same broken-baseline behaviour we already
    /// had, with the slight cost of a few extra bytes in <c>.opencode/config.json
    /// </c>. We treat that as acceptable: the worst case is "no improvement",
    /// not "regression".</para>
    ///
    /// <para><b>Field mapping.</b>
    /// <list type="bullet">
    ///   <item><b>Kimi K2.x</b> (Moonshot): <c>reasoning_content</c>.</item>
    ///   <item><b>DeepSeek V4 / R1 line</b>: <c>reasoning_content</c> (same
    ///         convention as Kimi).</item>
    ///   <item><b>GLM 5.x</b> (Zhipu): <c>reasoning_details</c> (Zhipu uses a
    ///         distinct field name).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Idempotent.</b> Re-running on a database that already has the
    /// values is a no-op — the WHERE filter matches the slug pattern and we
    /// overwrite with the same value. Operator-set custom options on these
    /// rows would be clobbered, but admin CRUD doesn't exist yet so that's not
    /// a current concern.</para>
    ///
    /// <para><b>Down.</b> Best-effort revert — sets the rows back to NULL.
    /// We don't try to restore intermediate values because there weren't any
    /// (this migration is the first non-null setting for these slugs).</para>
    /// </summary>
    /// <inheritdoc />
    public partial class SeedKimiDeepseekGlmReasoningOptions : Migration
    {
        private const string ReasoningContentOptions =
            "{\"interleaved\":{\"field\":\"reasoning_content\"}}";

        private const string ReasoningDetailsOptions =
            "{\"interleaved\":{\"field\":\"reasoning_details\"}}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kimi K2.x + DeepSeek V4 → both surface reasoning as
            // `reasoning_content` on the Chat Completions response. Same hint
            // value, two LIKE filters so the migration survives slug renames
            // on either family independently.
            migrationBuilder.Sql(
                $$"""
                UPDATE "OpencodeModels"
                SET "ReasoningOptions" = '{{ReasoningContentOptions}}'::jsonb
                WHERE ("Slug" LIKE 'kimi-%' OR "Slug" LIKE 'deepseek-%')
                  AND "IsDeleted" = false;
                """);

            // GLM 5.x → Zhipu surfaces reasoning as `reasoning_details`
            // (distinct field name from Kimi/DeepSeek).
            migrationBuilder.Sql(
                $$"""
                UPDATE "OpencodeModels"
                SET "ReasoningOptions" = '{{ReasoningDetailsOptions}}'::jsonb
                WHERE "Slug" LIKE 'glm-%'
                  AND "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert to NULL — the state before this migration's UP for these
            // rows. Scoped on slug pattern (not Id) so the down survives any
            // future seed reshuffle.
            migrationBuilder.Sql(
                """
                UPDATE "OpencodeModels"
                SET "ReasoningOptions" = NULL
                WHERE ("Slug" LIKE 'kimi-%'
                    OR "Slug" LIKE 'deepseek-%'
                    OR "Slug" LIKE 'glm-%')
                  AND "IsDeleted" = false;
                """);
        }
    }
}
