using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class DefaultRuntimePerformance2x4gb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RuntimeMemoryMb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 4096,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2048);

            migrationBuilder.AlterColumn<int>(
                name: "RuntimeCpus",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);

            migrationBuilder.AlterColumn<int>(
                name: "MemoryMb",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 4096,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2048);

            migrationBuilder.AlterColumn<int>(
                name: "Cpus",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);

            migrationBuilder.AlterColumn<string>(
                name: "CpuKind",
                table: "ProjectRuntimes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "performance",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "shared");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RuntimeMemoryMb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 2048,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 4096);

            migrationBuilder.AlterColumn<int>(
                name: "RuntimeCpus",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2);

            migrationBuilder.AlterColumn<int>(
                name: "MemoryMb",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 2048,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 4096);

            migrationBuilder.AlterColumn<int>(
                name: "Cpus",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 2);

            migrationBuilder.AlterColumn<string>(
                name: "CpuKind",
                table: "ProjectRuntimes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "shared",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "performance");
        }
    }
}
