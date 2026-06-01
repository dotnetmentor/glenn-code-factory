using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddOpencodeModelsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpencodeModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpencodeModels", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "OpencodeModels",
                columns: new[] { "Id", "CreatedAt", "DeletedAt", "DeletedBy", "DisplayName", "IsActive", "IsDeleted", "Slug", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a0000000-0000-0000-0000-000000000001"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "MiniMax M2.5 (Free)", true, false, "minimax-m2.5-free", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000002"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Ring 2.6 1T (Free)", true, false, "ring-2.6-1t-free", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000003"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "MiniMax M2.7", true, false, "minimax-m2.7", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000004"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "GPT-5.4 Mini", true, false, "gpt-5.4-mini", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("a0000000-0000-0000-0000-000000000005"), new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Qwen 3.5 Plus", true, false, "qwen-3.5-plus", new DateTime(2026, 5, 15, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpencodeModels");
        }
    }
}
