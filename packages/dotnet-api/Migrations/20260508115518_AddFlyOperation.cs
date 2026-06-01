using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddFlyOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FlyOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ResponsePayload = table.Column<string>(type: "jsonb", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlyOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlyOperations_RequestKey",
                table: "FlyOperations",
                column: "RequestKey");

            // "Latest ops per runtime" — CreatedAt sorted DESC so range scans pick up
            // newest rows first. EF Core 9 doesn't expose per-column sort direction,
            // so we emit the index via raw SQL (Postgres syntax).
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_FlyOperations_RuntimeId_CreatedAt\" " +
                "ON \"FlyOperations\" (\"RuntimeId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_FlyOperations_RuntimeId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "FlyOperations");
        }
    }
}
