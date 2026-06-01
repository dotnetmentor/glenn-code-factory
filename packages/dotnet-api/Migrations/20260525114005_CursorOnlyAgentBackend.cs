using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class CursorOnlyAgentBackend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_AgentModels_AgentModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_CursorModels_CursorModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_OpencodeModels_OpencodeModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_AgentModels_AgentModelId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_CursorModels_CursorModelId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_OpencodeModels_OpencodeModelId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "AgentModels");

            migrationBuilder.DropTable(
                name: "AgentNativesVersions");

            migrationBuilder.DropTable(
                name: "OpencodeModels");

            migrationBuilder.DropIndex(
                name: "IX_Projects_AgentModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_OpencodeModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_AgentModelId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_OpencodeModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "AgentBackend",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OpencodeModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedAnthropicApiKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedClaudeCodeOAuthToken",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedOpencodeZenApiKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentBackend",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "AgentModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ClaudeSessionId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "OpencodeSessionId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "OpencodeModelId",
                table: "AgentSessions");

            migrationBuilder.RenameColumn(
                name: "CursorModelId",
                table: "Projects",
                newName: "ModelId");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_CursorModelId",
                table: "Projects",
                newName: "IX_Projects_ModelId");

            migrationBuilder.RenameColumn(
                name: "CursorModelId",
                table: "AgentSessions",
                newName: "ModelId");

            migrationBuilder.RenameIndex(
                name: "IX_AgentSessions_CursorModelId",
                table: "AgentSessions",
                newName: "IX_AgentSessions_ModelId");

            migrationBuilder.RenameColumn(
                name: "CursorAgentId",
                table: "AgentSessions",
                newName: "AgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_CursorModels_ModelId",
                table: "AgentSessions",
                column: "ModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_CursorModels_ModelId",
                table: "Projects",
                column: "ModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_CursorModels_ModelId",
                table: "AgentSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_CursorModels_ModelId",
                table: "Projects");

            migrationBuilder.RenameColumn(
                name: "ModelId",
                table: "Projects",
                newName: "CursorModelId");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_ModelId",
                table: "Projects",
                newName: "IX_Projects_CursorModelId");

            migrationBuilder.RenameColumn(
                name: "ModelId",
                table: "AgentSessions",
                newName: "CursorModelId");

            migrationBuilder.RenameIndex(
                name: "IX_AgentSessions_ModelId",
                table: "AgentSessions",
                newName: "IX_AgentSessions_CursorModelId");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                table: "AgentSessions",
                newName: "CursorAgentId");

            migrationBuilder.AddColumn<string>(
                name: "AgentBackend",
                table: "Projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "claude");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpencodeModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedAnthropicApiKey",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedClaudeCodeOAuthToken",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedOpencodeZenApiKey",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentBackend",
                table: "AgentSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "claude");

            migrationBuilder.AddColumn<Guid>(
                name: "AgentModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaudeSessionId",
                table: "AgentSessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpencodeSessionId",
                table: "AgentSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpencodeModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystemDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentNativesVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BundleSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    BundleStorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentNativesVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpencodeModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ReasoningOptions = table.Column<string>(type: "jsonb", nullable: true),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpencodeModels", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AgentModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "IsSystemDefault", "Slug", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Balanced cost and capability — the default workhorse for most coding tasks.", "Claude Sonnet 4.7", true, false, true, "claude-sonnet-4-7", 10, new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "AgentModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "Description", "DisplayName", "IsActive", "IsDeleted", "Slug", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Highest-capability model — use when planning complex changes or debugging hard problems.", "Claude Opus 4.7", true, false, "claude-opus-4-7", 20, new DateTime(2026, 5, 13, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.InsertData(
                table: "OpencodeModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "DisplayName", "IsActive", "IsDeleted", "ReasoningOptions", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a0000000-0000-0000-0000-000000000001"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "MiniMax M2.5 (Free)", true, false, null, "minimax-m2.5-free", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000002"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Ring 2.6 1T (Free)", true, false, null, "ring-2.6-1t-free", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000003"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "MiniMax M2.7", true, false, null, "minimax-m2.7", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000004"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "GPT-5.4 Mini", true, false, null, "gpt-5.4-mini", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000005"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Qwen 3.5 Plus", true, false, null, "qwen-3.5-plus", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_AgentModelId",
                table: "Projects",
                column: "AgentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OpencodeModelId",
                table: "Projects",
                column: "OpencodeModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_AgentModelId",
                table: "AgentSessions",
                column: "AgentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_OpencodeModelId",
                table: "AgentSessions",
                column: "OpencodeModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_IsActive_SortOrder",
                table: "AgentModels",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_OnlyOneSystemDefault",
                table: "AgentModels",
                column: "IsSystemDefault",
                unique: true,
                filter: "\"IsSystemDefault\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_Slug",
                table: "AgentModels",
                column: "Slug",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_AgentNativesVersions_Channel_IsActive",
                table: "AgentNativesVersions",
                columns: new[] { "Channel", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentNativesVersions_Channel_Version",
                table: "AgentNativesVersions",
                columns: new[] { "Channel", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpencodeModels_IsActive",
                table: "OpencodeModels",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OpencodeModels_Slug",
                table: "OpencodeModels",
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
                name: "FK_AgentSessions_CursorModels_CursorModelId",
                table: "AgentSessions",
                column: "CursorModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_OpencodeModels_OpencodeModelId",
                table: "AgentSessions",
                column: "OpencodeModelId",
                principalTable: "OpencodeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_AgentModels_AgentModelId",
                table: "Projects",
                column: "AgentModelId",
                principalTable: "AgentModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_CursorModels_CursorModelId",
                table: "Projects",
                column: "CursorModelId",
                principalTable: "CursorModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_OpencodeModels_OpencodeModelId",
                table: "Projects",
                column: "OpencodeModelId",
                principalTable: "OpencodeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
