using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeSpecHealthRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RECOVERY MIGRATION (self-healing-runtime-specs, card B1).
            //
            // The SpecHealth column ALREADY EXISTS on the platform DB
            // (localhost:43594/app) — it was added by a prior, now-lost migration
            // whose orphaned history row survives. A fresh runtime DB, however,
            // won't have it. So instead of EF's generated AddColumn (which throws
            // "column already exists" on 43594) we use idempotent raw SQL:
            // ADD COLUMN IF NOT EXISTS is a no-op on 43594 and a real add on fresh
            // DBs. The model snapshot keeps tracking the column via the entity
            // config regardless, so EF's model-vs-snapshot diff stays clean.
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" ADD COLUMN IF NOT EXISTS \"SpecHealth\" character varying(16) NOT NULL DEFAULT 'Unknown';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"ProjectRuntimes\" DROP COLUMN IF EXISTS \"SpecHealth\";");
        }
    }
}
