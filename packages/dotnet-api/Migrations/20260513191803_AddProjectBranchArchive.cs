using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectBranchArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "ProjectBranches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "ProjectBranches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectBranches_ProjectId_IsArchived",
                table: "ProjectBranches",
                columns: new[] { "ProjectId", "IsArchived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProjectBranches_ProjectId_IsArchived",
                table: "ProjectBranches");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ProjectBranches");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "ProjectBranches");
        }
    }
}
