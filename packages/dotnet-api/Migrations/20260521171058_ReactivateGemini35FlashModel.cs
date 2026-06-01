using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Data-fix migration: reactivate the <c>gemini-3.5-flash</c> catalog row
    /// (Id <c>b0000000-...-000042</c>) that was previously soft-deleted by
    /// <c>20260519195312_RemoveFictionalGemini35FlashModel</c>.
    ///
    /// <para><b>Why now.</b> The earlier Remove migration retired this slug
    /// because, at the time, <c>gemini-3.5-flash</c> did not exist in the
    /// upstream opencode-zen / models.dev catalog and turns dispatched against
    /// it silently completed with zero AssistantText events and zero output
    /// tokens. That precondition no longer holds: <c>models.dev/api.json</c>
    /// now lists <c>opencode/gemini-3.5-flash</c> as a real model, and we've
    /// verified end-to-end that turns dispatched against it return real
    /// streamed text with non-zero output tokens. The original bug trigger
    /// (fictional slug) is gone, so the row should be back in the active set.</para>
    ///
    /// <para><b>Why a migration when the local DB is already patched.</b> The
    /// running database had its row manually reactivated by hand so the
    /// runtime is happy, but fresh environments — CI, new dev machines,
    /// preview deploys — replay migrations from scratch. Without this
    /// migration, those environments would re-soft-delete the row at startup
    /// and the active-models picker
    /// (<c>useGetApiOpencodeModelsActive</c>) wouldn't offer it. This
    /// migration makes "active again" part of the schema lineage.</para>
    ///
    /// <para><b>Idempotency.</b> The Up SQL is a single UPDATE filtered on the
    /// fixed seed Id. If the row exists in any state (soft-deleted, already
    /// reactivated, or freshly inserted by a future seeding pass) the WHERE
    /// matches and the columns are written to the desired terminal state. If
    /// the row was hard-deleted out of band, the WHERE matches zero rows and
    /// the statement is a natural no-op — no crash, no surprise insert. We
    /// deliberately do NOT re-INSERT here: the row's lifecycle is owned by
    /// <c>20260519191305_AddGemini35FlashModel</c>, and this migration only
    /// flips its soft-delete flags forward.</para>
    ///
    /// <para><b>Down.</b> Reverses the reactivation by restoring the exact
    /// state the Remove migration left behind: soft-delete the row and remap
    /// any Projects / in-flight AgentSessions pointing at the fictional Id
    /// back to the safe default <c>claude-sonnet-4-5</c>
    /// (<c>b0000000-...-000011</c>) — same fallback policy as the original
    /// Remove migration. Completed sessions keep their historical pointer so
    /// the audit trail survives.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class ReactivateGemini35FlashModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flip the soft-delete flags on the gemini-3.5-flash catalog row
            // back to "active". WHERE on the fixed seed Id makes this naturally
            // idempotent — re-runs are no-ops, and a missing row (manually
            // hard-deleted) matches zero rows instead of crashing. We don't
            // re-INSERT because the row's existence is owned upstream by the
            // original AddGemini35FlashModel migration; this is purely a
            // forward data-fix on its soft-delete columns.
            migrationBuilder.Sql(
                """
                UPDATE "OpencodeModels"
                SET "IsActive" = true,
                    "IsDeleted" = false,
                    "DeletedAt" = NULL,
                    "DeletedBy" = NULL,
                    "UpdatedAt" = NOW() AT TIME ZONE 'UTC'
                WHERE "Id" = 'b0000000-0000-0000-0000-000000000042';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the exact state the Remove migration left behind so a
            // rollback returns the system to its pre-reactivation shape.

            // 1a. Remap any Project pinned to the gemini-3.5-flash Id back to
            // the safe default (claude-sonnet-4-5) — mirrors the Remove
            // migration's policy. The user's next chat turn will then dispatch
            // against the fallback instead of a model the rolled-back system
            // considers retired.
            migrationBuilder.Sql(
                """
                UPDATE "Projects"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000042';
                """);

            // 1b. Remap in-flight AgentSessions (Pending / Running) only.
            // Completed sessions (Succeeded / Failed / Cancelled) keep their
            // historical pointer so the audit trace is preserved — same
            // filter the Remove migration applied.
            migrationBuilder.Sql(
                """
                UPDATE "AgentSessions"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000042'
                  AND "Status" IN ('Pending', 'Running');
                """);

            // 2. Re-soft-delete the catalog row with the same DeletedBy marker
            // the Remove migration used, so rolling back leaves an
            // indistinguishable on-disk state.
            migrationBuilder.Sql(
                """
                UPDATE "OpencodeModels"
                SET "IsActive" = false,
                    "IsDeleted" = true,
                    "DeletedAt" = NOW() AT TIME ZONE 'UTC',
                    "DeletedBy" = 'RemoveFictionalGemini35FlashModel',
                    "UpdatedAt" = NOW() AT TIME ZONE 'UTC'
                WHERE "Id" = 'b0000000-0000-0000-0000-000000000042';
                """);
        }
    }
}
