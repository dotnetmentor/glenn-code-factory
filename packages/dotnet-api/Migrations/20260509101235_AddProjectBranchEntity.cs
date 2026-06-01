using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectBranchEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ProjectRuntimes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Conversations.BranchId is being promoted from a free-form string
            // to a real FK to ProjectBranch. Postgres can't implicitly cast
            // varchar -> uuid, so the only sane move on a dev database is
            // drop + recreate the column. Per the e2e-smoketest spec this is
            // explicitly OK: dev data is throwaway, and there are no
            // production rows to preserve. The composite IX_Conversations_
            // ProjectId_BranchId index is automatically dropped by Postgres
            // when its column is dropped and re-created below.
            migrationBuilder.DropIndex(
                name: "IX_Conversations_ProjectId_BranchId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Re-create the composite index that EF tracks on (ProjectId, BranchId).
            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId_BranchId",
                table: "Conversations",
                columns: new[] { "ProjectId", "BranchId" });

            migrationBuilder.CreateTable(
                name: "ProjectBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectBranches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectBranches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_BranchId",
                table: "ProjectRuntimes",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BranchId",
                table: "Conversations",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBranches_ProjectId_Name",
                table: "ProjectBranches",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_ProjectBranches_BranchId",
                table: "Conversations",
                column: "BranchId",
                principalTable: "ProjectBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectRuntimes_ProjectBranches_BranchId",
                table: "ProjectRuntimes",
                column: "BranchId",
                principalTable: "ProjectBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_ProjectBranches_BranchId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectRuntimes_ProjectBranches_BranchId",
                table: "ProjectRuntimes");

            migrationBuilder.DropTable(
                name: "ProjectBranches");

            migrationBuilder.DropIndex(
                name: "IX_ProjectRuntimes_BranchId",
                table: "ProjectRuntimes");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_BranchId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ProjectId_BranchId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Conversations");

            migrationBuilder.AddColumn<string>(
                name: "BranchId",
                table: "Conversations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "main");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId_BranchId",
                table: "Conversations",
                columns: new[] { "ProjectId", "BranchId" });
        }
    }
}
