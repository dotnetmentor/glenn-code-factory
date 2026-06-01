using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionCostMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CacheWriteTokens",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReasoningTokens",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalCostUsd",
                table: "AgentSessions",
                type: "numeric(12,8)",
                precision: 12,
                scale: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "ReasoningTokens",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "TotalCostUsd",
                table: "AgentSessions");
        }
    }
}
