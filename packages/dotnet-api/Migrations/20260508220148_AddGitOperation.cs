using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddGitOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpType = table.Column<int>(type: "integer", nullable: false),
                    CommandLine = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    OutputTail = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    OutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WasDestructive = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitOperations_ProjectRuntimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "ProjectRuntimes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitOperations_ConversationId",
                table: "GitOperations",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_GitOperations_Runtime_StartedAt",
                table: "GitOperations",
                columns: new[] { "RuntimeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GitOperations_RuntimeId",
                table: "GitOperations",
                column: "RuntimeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitOperations");
        }
    }
}
