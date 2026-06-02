using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Source.Infrastructure;

#nullable disable

namespace api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260601113000_AddWorkspaceCursorByok")]
    /// <inheritdoc />
    public partial class AddWorkspaceCursorByok : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowProjectCursorApiKeyOverride",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCursorApiKey",
                table: "Workspaces",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkspaceKeyMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WrappedDek = table.Column<byte[]>(type: "bytea", nullable: false),
                    MasterKeyVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceKeyMaterials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceKeyMaterials_WorkspaceId",
                table: "WorkspaceKeyMaterials",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceKeyMaterials");

            migrationBuilder.DropColumn(
                name: "AllowProjectCursorApiKeyOverride",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "EncryptedCursorApiKey",
                table: "Workspaces");
        }
    }
}
