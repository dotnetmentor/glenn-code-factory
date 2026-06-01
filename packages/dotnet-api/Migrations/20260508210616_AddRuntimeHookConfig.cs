using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeHookConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeHookConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeHookConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuntimeHookConfigs_ProjectRuntimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "ProjectRuntimes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeHookConfigs_RuntimeId",
                table: "RuntimeHookConfigs",
                column: "RuntimeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeHookConfigs");
        }
    }
}
