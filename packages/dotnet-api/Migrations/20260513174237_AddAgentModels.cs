using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentModels", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AgentModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Balanced cost and capability — the default workhorse for most coding tasks.", "Claude Sonnet 4.7", true, false, "claude-sonnet-4-7", 10, new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Highest-capability model — use when planning complex changes or debugging hard problems.", "Claude Opus 4.7", true, false, "claude-opus-4-7", 20, new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_AgentModelId",
                table: "Projects",
                column: "AgentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_AgentModelId",
                table: "AgentSessions",
                column: "AgentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_IsActive_SortOrder",
                table: "AgentModels",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_Slug",
                table: "AgentModels",
                column: "Slug",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_AgentModels_AgentModelId",
                table: "AgentSessions",
                column: "AgentModelId",
                principalTable: "AgentModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_AgentModels_AgentModelId",
                table: "Projects",
                column: "AgentModelId",
                principalTable: "AgentModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_AgentModels_AgentModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_AgentModels_AgentModelId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "AgentModels");

            migrationBuilder.DropIndex(
                name: "IX_Projects_AgentModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_AgentModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "AgentModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentModelId",
                table: "AgentSessions");
        }
    }
}
