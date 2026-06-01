using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeStateEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeStateEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TriggeredBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Metadata = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeStateEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeStateEvents_ToState",
                table: "RuntimeStateEvents",
                column: "ToState");

            // "Latest transitions per runtime" — CreatedAt sorted DESC so range
            // scans pick up newest rows first. EF Core 9 doesn't expose
            // per-column sort direction, so we emit the index via raw SQL
            // (Postgres syntax). Mirrors the AddFlyOperation pattern.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeStateEvents_RuntimeId_CreatedAt\" " +
                "ON \"RuntimeStateEvents\" (\"RuntimeId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeStateEvents_RuntimeId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "RuntimeStateEvents");
        }
    }
}
