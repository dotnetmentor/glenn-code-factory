using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Data-fix migration: remap opencode-backed Projects (and in-flight
    /// AgentSessions) whose OpencodeModelId was nulled by a prior migration.
    ///
    /// <para><b>Cause.</b> The previous migration
    /// <c>20260517060143_ReplaceOpencodeModelsSeed</c> hard-deleted the
    /// original opencode-models seed rows (the a0000000-... namespace) before
    /// inserting the new curated catalog (b0000000-... namespace). The
    /// foreign keys Projects.OpencodeModelId and AgentSessions.OpencodeModelId
    /// are configured ON DELETE SET NULL, so every existing reference to the
    /// deleted rows was nulled with no remap step. The bootstrap-opencode
    /// daemon stage then failed with "no model slug configured"
    /// (non-recoverable) on every affected project.</para>
    ///
    /// <para><b>Fix.</b> Set the nulled references to a sensible default from
    /// the new catalog (claude-sonnet-4.5 → <c>b0000000-...-000011</c>). The
    /// WHERE clauses target NULL only, so re-running this on an already-
    /// patched database is a no-op. AgentSessions are scoped to the in-flight
    /// statuses (<c>Pending</c> waiting in the soft-queue, <c>Running</c> on
    /// the daemon) so we never rewrite history on completed sessions.</para>
    ///
    /// <para><b>Down is a no-op</b> — rolling back a data fix doesn't make
    /// sense; the original (broken) state was unrecoverable in the first
    /// place.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddOpencodeModelBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill Projects: opencode-backed rows whose OpencodeModelId was
            // nulled by the ReplaceOpencodeModelsSeed cascade.
            migrationBuilder.Sql(
                """
                UPDATE "Projects"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "AgentBackend" = 'opencode'
                  AND "OpencodeModelId" IS NULL;
                """);

            // Backfill in-flight AgentSessions only. Completed sessions
            // (Succeeded / Failed / Cancelled / etc.) are left untouched —
            // their historical record stays accurate even if the model ref
            // is now NULL.
            migrationBuilder.Sql(
                """
                UPDATE "AgentSessions"
                SET "OpencodeModelId" = 'b0000000-0000-0000-0000-000000000011',
                    "UpdatedAt" = NOW()
                WHERE "AgentBackend" = 'opencode'
                  AND "OpencodeModelId" IS NULL
                  AND "Status" IN ('Pending', 'Running');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op. This is a data fix; the previous state
            // (NULL refs causing bootstrap failures) was a bug, not something
            // to restore. Rolling back this migration leaves the corrected
            // refs in place.
        }
    }
}
