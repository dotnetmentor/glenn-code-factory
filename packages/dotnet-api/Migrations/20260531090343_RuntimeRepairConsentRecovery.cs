using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeRepairConsentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RECOVERY MIGRATION (self-healing-runtime-specs, cards B2/B3).
            //
            // All five repair-consent columns ALREADY EXIST on the platform DB
            // (localhost:43594/app) — added by a prior, now-lost migration whose
            // orphaned history rows survive. A fresh runtime DB won't have them.
            // So instead of EF's generated AddColumn (which throws "column already
            // exists" on 43594) we use idempotent raw SQL: ADD COLUMN IF NOT EXISTS
            // is a no-op on 43594 and a real add on fresh DBs. The model snapshot
            // still tracks the columns via the entity config, so EF's
            // model-vs-snapshot diff stays clean and fresh DBs are correct.
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"AutoApplyNextProposal\" boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"AutoApplyExpiresAt\" timestamp with time zone NULL;");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"AutoApplyAttemptsRemaining\" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"RepairAttempts\" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"LastRepairAttemptAt\" timestamp with time zone NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"AutoApplyNextProposal\";");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"AutoApplyExpiresAt\";");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"AutoApplyAttemptsRemaining\";");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"RepairAttempts\";");
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"LastRepairAttemptAt\";");
        }
    }
}
