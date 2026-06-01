using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddCursorBackendSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CursorModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCursorApiKey",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CursorAgentId",
                table: "AgentSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CursorModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CursorModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CursorModels", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "CursorModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("b0000000-0000-0000-0000-000000000001"), new DateTime(2026, 5, 23, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, "Composer 2", true, false, "composer-2", new DateTime(2026, 5, 23, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("b0000000-0000-0000-0000-000000000002"), new DateTime(2026, 5, 23, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, "GPT-5.5", true, false, "gpt-5.5", new DateTime(2026, 5, 23, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CursorModelId",
                table: "Projects",
                column: "CursorModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_CursorModelId",
                table: "AgentSessions",
                column: "CursorModelId");

            migrationBuilder.CreateIndex(
                name: "IX_CursorModels_IsActive",
                table: "CursorModels",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CursorModels_Slug",
                table: "CursorModels",
                column: "Slug",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_CursorModels_CursorModelId",
                table: "AgentSessions",
                column: "CursorModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_CursorModels_CursorModelId",
                table: "Projects",
                column: "CursorModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_CursorModels_CursorModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_CursorModels_CursorModelId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "CursorModels");

            migrationBuilder.DropIndex(
                name: "IX_Projects_CursorModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_CursorModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "CursorModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedCursorApiKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CursorAgentId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "CursorModelId",
                table: "AgentSessions");
        }
    }
}
