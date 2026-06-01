using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddCardProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedOnBranch",
                table: "ProjectKanbanCards",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "ProjectKanbanCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedOnBranch",
                table: "ProjectKanbanCards");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ProjectKanbanCards");
        }
    }
}
