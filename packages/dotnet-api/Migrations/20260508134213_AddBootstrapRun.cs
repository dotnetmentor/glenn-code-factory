using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddBootstrapRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BootstrapRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DaemonVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BootstrapVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BootstrapRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BootstrapRuns_Success",
                table: "BootstrapRuns",
                column: "Success");

            // "Latest boots per runtime" — StartedAt sorted DESC so range scans
            // pick up newest rows first. EF Core 9 doesn't expose per-column sort
            // direction, so we emit the index via raw SQL (Postgres syntax).
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_BootstrapRuns_RuntimeId_StartedAt\" " +
                "ON \"BootstrapRuns\" (\"RuntimeId\", \"StartedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_BootstrapRuns_RuntimeId_StartedAt\";");

            migrationBuilder.DropTable(
                name: "BootstrapRuns");
        }
    }
}
