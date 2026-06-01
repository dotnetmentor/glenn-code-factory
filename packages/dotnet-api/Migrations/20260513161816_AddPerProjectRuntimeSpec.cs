using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddPerProjectRuntimeSpec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeCpuKind",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "shared");

            migrationBuilder.AddColumn<int>(
                name: "RuntimeCpus",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "RuntimeMemoryMb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 2048);

            migrationBuilder.AddColumn<int>(
                name: "RuntimeVolumeSizeGb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "CpuKind",
                table: "ProjectRuntimes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "shared");

            migrationBuilder.AddColumn<int>(
                name: "Cpus",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MemoryMb",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 2048);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeCpuKind",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RuntimeCpus",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RuntimeMemoryMb",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RuntimeVolumeSizeGb",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CpuKind",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "Cpus",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "MemoryMb",
                table: "ProjectRuntimes");
        }
    }
}
