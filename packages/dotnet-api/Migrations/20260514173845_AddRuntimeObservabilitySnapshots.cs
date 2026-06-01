using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeObservabilitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastDiskSampledAt",
                table: "ProjectRuntimes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastDiskTotalBytes",
                table: "ProjectRuntimes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastDiskUsedBytes",
                table: "ProjectRuntimes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSupervisordSnapshot",
                table: "ProjectRuntimes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSysstatsSnapshot",
                table: "ProjectRuntimes",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastDiskSampledAt",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "LastDiskTotalBytes",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "LastDiskUsedBytes",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "LastSupervisordSnapshot",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "LastSysstatsSnapshot",
                table: "ProjectRuntimes");
        }
    }
}
