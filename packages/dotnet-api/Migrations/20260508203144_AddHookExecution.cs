using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddHookExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HookExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    HookPoint = table.Column<int>(type: "integer", nullable: false),
                    HookName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cmd = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    OutputTail = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    OutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FeedbackMode = table.Column<int>(type: "integer", nullable: false),
                    WasConfigError = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HookExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HookExecutions_ProjectRuntimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "ProjectRuntimes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HookExecutions_ConversationId",
                table: "HookExecutions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_HookExecutions_Runtime_StartedAt",
                table: "HookExecutions",
                columns: new[] { "RuntimeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HookExecutions_RuntimeId",
                table: "HookExecutions",
                column: "RuntimeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HookExecutions");
        }
    }
}
