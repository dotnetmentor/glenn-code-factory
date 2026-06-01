using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Data-fix migration: retire the fictional <c>gemini-3.5-flash</c> catalog
    /// row (Id <c>b0000000-...-000042</c>) added earlier today by
    /// <c>20260519191305_AddGemini35FlashModel</c>, and remap any live
    /// references to a real model.
    ///
    /// <para><b>Cause.</b> The slug <c>gemini-3.5-flash</c> does not exist in
    /// the upstream opencode-zen model catalog — it was a fabricated name. When
    /// a session is dispatched against a model the daemon can't resolve
    /// upstream, the request silently completes with zero AssistantText events
    /// and zero output tokens, yet the session is still marked
    /// <c>Succeeded</c>. The user-facing symptom is an empty AI response in the
    /// chat panel. Evidence: completed sessions on this model show 0-token
    /// usage in the AgentEvents trace while sibling sessions in the same
    /// conversation on real models (claude-sonnet-4-5) produced normal
    /// streamed output.</para>
    ///
    /// <para><b>Why not just <c>Down()</c> the original migration.</b> Three
    /// completed AgentSessions already reference this Id via
    /// <c>AgentSessions.OpencodeModelId</c>. Those FKs are configured
    /// <c>ON DELETE SET NULL</c>, so a hard delete would null out the audit
    /// trail for sessions that legitimately ran (even though they produced
    /// nothing useful). We want the historical record of "this session was
    /// dispatched against the fictional slug" preserved for analytics. We also
    /// cannot rewind an already-deployed migration without rewriting history.
    /// The right shape is a forward-only data fix.</para>
    ///
    /// <para><b>Fix shape.</b>
    /// (1) Remap the one live <c>Project</c> currently pointing at this Id, and
    /// any in-flight <c>AgentSession</c> rows (<c>Pending</c>/<c>Running</c>),
    /// to <c>claude-sonnet-4-5</c> (Id <c>b0000000-...-000011</c>) — the same
    /// safe default the prior <c>AddOpencodeModelBackfill</c> migration chose.
    /// Completed sessions are deliberately left alone so the audit story stays
    /// intact (mirrors the in-flight-only filter from that earlier migration).
    /// (2) Soft-delete the catalog row (<c>IsDeleted=true</c>,
    /// <c>IsActive=false</c>, <c>DeletedAt=NOW()</c>,
    /// <c>DeletedBy='system:remove-fictional-slug'</c>) so the active-models
    /// picker (<c>useGetApiOpencodeModelsActive</c>) stops offering it but the
    /// FKs on the 3 completed sessions remain valid.</para>
    ///
    /// <para>The WHERE clauses are idempotent — re-running this on an
    /// already-patched database is a no-op for both the remap and the
    /// soft-delete (filter on Id; soft-delete column writes are stable).</para>
    ///
    /// <para><b>Latent daemon bug.</b> A session that produces zero
    /// AssistantText events should not be marked <c>Succeeded</c>. That's a
    /// separate concern — tracked outside this card — and is intentionally not
    /// touched here. This migration only removes the trigger; the daemon's
    /// "silent success" classifier needs its own fix.</para>
    ///
    /// <para><b>Down is a no-op</b> — this is a data fix removing a fictional
    /// slug. There is nothing meaningful to restore, and reinstating the slug
    /// would just re-trigger the empty-response bug.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class RemoveFictionalGemini35FlashModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1a. Remap any Project pinned to the fictional slug to the safe
            // default (claude-sonnet-4-5, b0000000-...-000011) — same default
            // chosen by the earlier AddOpencodeModelBackfill migration. The
            // user's next chat turn will dispatch against a real model instead
            // of silently producing zero output.
            migrationBuilder.Sql(
                """
                UPDATE "Projects"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000042';
                """);

            // 1b. Remap in-flight AgentSessions only (Pending in the soft-queue,
            // Running on the daemon). Completed sessions — Succeeded / Failed /
            // Cancelled — are deliberately left pointing at the fictional Id;
            // the soft-delete below keeps that FK valid and preserves the
            // historical "this session was dispatched against the fictional
            // slug" trace for analytics. Mirrors the filter used by
            // AddOpencodeModelBackfill.
            migrationBuilder.Sql(
                """
                UPDATE "AgentSessions"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000042'
                  AND "Status" IN ('Pending', 'Running');
                """);

            // 2. Soft-delete the catalog row. We do NOT hard-delete: the FKs on
            // the 3 completed AgentSessions are ON DELETE SET NULL, and we want
            // their model-ref preserved for the audit trail. Soft-delete hides
            // the row from the active-models picker without breaking history.
            migrationBuilder.Sql(
                """
                UPDATE "OpencodeModels"
                SET "IsDeleted" = true,
                    "IsActive" = false,
                    "DeletedAt" = NOW(),
                    "DeletedBy" = 'system:remove-fictional-slug',
                    "UpdatedAt" = NOW()
                WHERE "Id" = 'b0000000-0000-0000-0000-000000000042';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op. This is a forward-only data fix retiring a
            // fictional slug that never existed upstream. Rolling back would
            // re-expose the empty-response bug. The companion migration
            // 20260519191305_AddGemini35FlashModel still owns the catalog row
            // lifecycle in the EF model; we only mutate its soft-delete flags
            // here.
        }
    }
}
