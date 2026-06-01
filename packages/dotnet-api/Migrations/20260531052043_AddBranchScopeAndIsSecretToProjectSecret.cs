using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchScopeAndIsSecretToProjectSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProjectSecrets_ProjectId_Key",
                table: "ProjectSecrets");

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ProjectSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSecret",
                table: "ProjectSecrets",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecrets_BranchId",
                table: "ProjectSecrets",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecrets_ProjectId_BranchId_Key",
                table: "ProjectSecrets",
                columns: new[] { "ProjectId", "BranchId", "Key" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectSecrets_ProjectBranches_BranchId",
                table: "ProjectSecrets",
                column: "BranchId",
                principalTable: "ProjectBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectSecrets_ProjectBranches_BranchId",
                table: "ProjectSecrets");

            migrationBuilder.DropIndex(
                name: "IX_ProjectSecrets_BranchId",
                table: "ProjectSecrets");

            migrationBuilder.DropIndex(
                name: "IX_ProjectSecrets_ProjectId_BranchId_Key",
                table: "ProjectSecrets");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ProjectSecrets");

            migrationBuilder.DropColumn(
                name: "IsSecret",
                table: "ProjectSecrets");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecrets_ProjectId_Key",
                table: "ProjectSecrets",
                columns: new[] { "ProjectId", "Key" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }
    }
}
