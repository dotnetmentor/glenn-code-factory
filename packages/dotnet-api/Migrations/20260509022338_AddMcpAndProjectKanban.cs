using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpAndProjectKanban : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequestSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ResponseSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectKanbanCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectKanbanCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectKanbanCards_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpServers_Name",
                table: "McpServers",
                column: "Name",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKanbanCards_CreatedBy",
                table: "ProjectKanbanCards",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKanbanCards_ProjectId_Status_Position",
                table: "ProjectKanbanCards",
                columns: new[] { "ProjectId", "Status", "Position" });

            // "Latest calls for this runtime" — CreatedAt sorted DESC so range
            // scans pick up newest rows first. EF Core 9 doesn't expose per-
            // column sort direction, so we emit the index via raw SQL (Postgres
            // syntax). Mirrors the FlyOperation / BootstrapRun /
            // RuntimeStateEvent / SecretAuditEvent precedent.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_McpCalls_RuntimeId_CreatedAt\" " +
                "ON \"McpCalls\" (\"RuntimeId\", \"CreatedAt\" DESC);");

            // "Latest calls for this (server, method)" — dashboards / abuse
            // forensics. DESC on CreatedAt for the same reason as above.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_McpCalls_ServerName_Method_CreatedAt\" " +
                "ON \"McpCalls\" (\"ServerName\", \"Method\", \"CreatedAt\" DESC);");

            // Seed the kanban MCP catalog row. Fixed Guid so the seed is
            // deterministic across deployments — future cards / tests can
            // reference this id directly. CreatedAt / UpdatedAt set to a
            // fixed UTC timestamp so the migration is reproducible (the
            // SaveChanges-based interceptor doesn't run for raw seeds).
            migrationBuilder.InsertData(
                table: "McpServers",
                columns: new[] { "Id", "Name", "Version", "DefaultEnabled", "CreatedAt", "UpdatedAt", "IsDeleted", "DeletedAt", "DeletedBy" },
                values: new object[]
                {
                    Guid.Parse("c748477b-a644-461f-ac9d-04f739b29f6b"),
                    "kanban",
                    "v1",
                    true,
                    new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc),
                    false,
                    null,
                    null,
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_McpCalls_ServerName_Method_CreatedAt\";");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_McpCalls_RuntimeId_CreatedAt\";");

            migrationBuilder.DeleteData(
                table: "McpServers",
                keyColumn: "Id",
                keyValue: Guid.Parse("c748477b-a644-461f-ac9d-04f739b29f6b"));

            migrationBuilder.DropTable(
                name: "McpCalls");

            migrationBuilder.DropTable(
                name: "McpServers");

            migrationBuilder.DropTable(
                name: "ProjectKanbanCards");
        }
    }
}
