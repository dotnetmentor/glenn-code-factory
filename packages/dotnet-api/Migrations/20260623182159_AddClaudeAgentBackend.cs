using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddClaudeAgentBackend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaudeModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedAnthropicApiKey",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentBackend",
                table: "Conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "cursor");

            migrationBuilder.AddColumn<string>(
                name: "AgentBackend",
                table: "AgentSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "cursor");

            migrationBuilder.AddColumn<Guid>(
                name: "ClaudeModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaudeSessionId",
                table: "AgentSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "AgentSessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClaudeModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSystemDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SupportsReasoning = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DefaultEffort = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
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
                    table.PrimaryKey("PK_ClaudeModels", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ClaudeModels",
                columns: new[] { "Id", "CreatedAt", "DefaultEffort", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "IsSystemDefault", "Slug", "SupportsReasoning", "UpdatedAt" },
                values: new object[] { new Guid("c1a0de00-0000-0000-0000-000000000001"), new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc), "high", null, null, null, "Claude Opus 4.8", true, false, true, "claude-opus-4-8", true, new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "ClaudeModels",
                columns: new[] { "Id", "CreatedAt", "DefaultEffort", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "SortOrder", "SupportsReasoning", "UpdatedAt" },
                values: new object[] { new Guid("c1a0de00-0000-0000-0000-000000000002"), new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc), "high", null, null, null, "Claude Sonnet 4.6", true, false, "claude-sonnet-4-6", 10, true, new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "ClaudeModels",
                columns: new[] { "Id", "CreatedAt", "DefaultEffort", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("c1a0de00-0000-0000-0000-000000000003"), new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, "Claude Haiku 4.5", true, false, "claude-haiku-4-5", 20, new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "ClaudeModels",
                columns: new[] { "Id", "CreatedAt", "DefaultEffort", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "SortOrder", "SupportsReasoning", "UpdatedAt" },
                values: new object[] { new Guid("c1a0de00-0000-0000-0000-000000000004"), new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc), "high", null, null, null, "Claude Fable 5", true, false, "claude-fable-5", 30, true, new DateTime(2026, 6, 23, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ClaudeModelId",
                table: "Projects",
                column: "ClaudeModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ClaudeModelId",
                table: "AgentSessions",
                column: "ClaudeModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeModels_IsActive",
                table: "ClaudeModels",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeModels_OnlyOneSystemDefault",
                table: "ClaudeModels",
                column: "IsSystemDefault",
                unique: true,
                filter: "\"IsSystemDefault\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeModels_Slug",
                table: "ClaudeModels",
                column: "Slug",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_ClaudeModels_ClaudeModelId",
                table: "AgentSessions",
                column: "ClaudeModelId",
                principalTable: "ClaudeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_ClaudeModels_ClaudeModelId",
                table: "Projects",
                column: "ClaudeModelId",
                principalTable: "ClaudeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_ClaudeModels_ClaudeModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_ClaudeModels_ClaudeModelId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "ClaudeModels");

            migrationBuilder.DropIndex(
                name: "IX_Projects_ClaudeModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_ClaudeModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaudeModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedAnthropicApiKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentBackend",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AgentBackend",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaudeModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaudeSessionId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "AgentSessions");
        }
    }
}
