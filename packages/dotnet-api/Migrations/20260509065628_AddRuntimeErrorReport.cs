using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeErrorReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeErrorReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StackTrace = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    Context = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    ReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeErrorReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeErrorReports_Category",
                table: "RuntimeErrorReports",
                column: "Category");

            // "Latest errors per runtime" — CreatedAt sorted DESC so range
            // scans pick up newest rows first. EF Core 9 doesn't expose
            // per-column sort direction on relational indexes, so we emit
            // the index via raw SQL (Postgres syntax). Same idiom as the
            // BootstrapRun + RuntimeStateEvent + RuntimeProposal indexes.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeErrorReports_RuntimeId_CreatedAt\" " +
                "ON \"RuntimeErrorReports\" (\"RuntimeId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeErrorReports_RuntimeId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "RuntimeErrorReports");
        }
    }
}
