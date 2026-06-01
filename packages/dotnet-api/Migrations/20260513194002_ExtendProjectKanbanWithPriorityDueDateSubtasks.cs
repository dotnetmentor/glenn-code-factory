using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class ExtendProjectKanbanWithPriorityDueDateSubtasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "ProjectKanbanCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "ProjectKanbanCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ProjectKanbanCardSubtasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKanbanCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectKanbanCardSubtasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectKanbanCardSubtasks_ProjectKanbanCards_ProjectKanbanC~",
                        column: x => x.ProjectKanbanCardId,
                        principalTable: "ProjectKanbanCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKanbanCardSubtasks_CardId_Position",
                table: "ProjectKanbanCardSubtasks",
                columns: new[] { "ProjectKanbanCardId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectKanbanCardSubtasks");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "ProjectKanbanCards");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ProjectKanbanCards");
        }
    }
}
