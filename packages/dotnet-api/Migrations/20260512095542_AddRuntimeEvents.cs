using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Creates the <c>RuntimeEvents</c> table backing the runtime drawer's
    /// Timeline tab. Append-only structured event store with a per-runtime
    /// rolling FIFO cap enforced in <c>RecordRuntimeEventCommandHandler</c>.
    ///
    /// <para>Indexes are DESC on the time / duration column so range scans pick
    /// up the newest / slowest rows first. EF Core 9 doesn't expose per-column
    /// sort direction on relational indexes, so we emit them via raw SQL
    /// (Postgres syntax) — same idiom as <c>RuntimeStateEvent</c> /
    /// <c>RuntimeErrorReport</c> / <c>BootstrapRun</c>.</para>
    /// </summary>
    public partial class AddRuntimeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeEvents", x => x.Id);
                });

            // Timeline default: "last N events per runtime, newest first".
            // Re-emitted as DESC via raw SQL since EF can't express it on a
            // composite relational index.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeEvents_RuntimeId_Timestamp\" " +
                "ON \"RuntimeEvents\" (\"RuntimeId\", \"Timestamp\" DESC);");

            // Filtered Timeline: "only events of Type T for runtime X, newest first".
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeEvents_RuntimeId_Type_Timestamp\" " +
                "ON \"RuntimeEvents\" (\"RuntimeId\", \"Type\", \"Timestamp\" DESC);");

            // "Slowest events" — partial index on non-null DurationMs only,
            // DESC so the top of a range scan is the slowest.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeEvents_RuntimeId_DurationMs\" " +
                "ON \"RuntimeEvents\" (\"RuntimeId\", \"DurationMs\" DESC) " +
                "WHERE \"DurationMs\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_RuntimeEvents_RuntimeId_DurationMs\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_RuntimeEvents_RuntimeId_Type_Timestamp\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_RuntimeEvents_RuntimeId_Timestamp\";");

            migrationBuilder.DropTable(
                name: "RuntimeEvents");
        }
    }
}
