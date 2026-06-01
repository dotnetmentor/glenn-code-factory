using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class SoftDetachProjectFromGithubInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_GithubInstallations_GithubInstallationId",
                table: "Projects");

            migrationBuilder.AlterColumn<Guid>(
                name: "GithubInstallationId",
                table: "Projects",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_GithubInstallations_GithubInstallationId",
                table: "Projects",
                column: "GithubInstallationId",
                principalTable: "GithubInstallations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_GithubInstallations_GithubInstallationId",
                table: "Projects");

            migrationBuilder.AlterColumn<Guid>(
                name: "GithubInstallationId",
                table: "Projects",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_GithubInstallations_GithubInstallationId",
                table: "Projects",
                column: "GithubInstallationId",
                principalTable: "GithubInstallations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
