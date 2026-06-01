using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAgentPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectAgentPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionMode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AllowDangerouslySkipPermissions = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedTools = table.Column<List<string>>(type: "jsonb", nullable: false),
                    DisallowedTools = table.Column<List<string>>(type: "jsonb", nullable: false),
                    AdditionalDirectories = table.Column<List<string>>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAgentPermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAgentPermissions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAgentPermissions_ProjectId",
                table: "ProjectAgentPermissions",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectAgentPermissions");
        }
    }
}
