using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccessTokenToGithubInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserAccessToken",
                table: "GithubInstallations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UserAccessTokenExpiresAt",
                table: "GithubInstallations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserLogin",
                table: "GithubInstallations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserRefreshToken",
                table: "GithubInstallations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UserRefreshTokenExpiresAt",
                table: "GithubInstallations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserAccessToken",
                table: "GithubInstallations");

            migrationBuilder.DropColumn(
                name: "UserAccessTokenExpiresAt",
                table: "GithubInstallations");

            migrationBuilder.DropColumn(
                name: "UserLogin",
                table: "GithubInstallations");

            migrationBuilder.DropColumn(
                name: "UserRefreshToken",
                table: "GithubInstallations");

            migrationBuilder.DropColumn(
                name: "UserRefreshTokenExpiresAt",
                table: "GithubInstallations");
        }
    }
}
