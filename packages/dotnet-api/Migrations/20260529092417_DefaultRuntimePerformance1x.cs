using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class DefaultRuntimePerformance1x : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RuntimeVolumeSizeGb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 10,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 5);

            migrationBuilder.AlterColumn<string>(
                name: "RuntimeCpuKind",
                table: "Projects",
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
                name: "RuntimeVolumeSizeGb",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 10);

            migrationBuilder.AlterColumn<string>(
                name: "RuntimeCpuKind",
                table: "Projects",
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
