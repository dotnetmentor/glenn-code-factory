using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeTokenUsageMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "RuntimeTokenIssues",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RequestCount",
                table: "RuntimeTokenIssues",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeTokenIssues_LastUsedAt",
                table: "RuntimeTokenIssues",
                column: "LastUsedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RuntimeTokenIssues_LastUsedAt",
                table: "RuntimeTokenIssues");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "RuntimeTokenIssues");

            migrationBuilder.DropColumn(
                name: "RequestCount",
                table: "RuntimeTokenIssues");
        }
    }
}
