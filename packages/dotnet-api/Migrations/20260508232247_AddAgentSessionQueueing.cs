using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionQueueing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "AgentSessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueuePosition",
                table: "AgentSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RuntimeId",
                table: "AgentSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill RuntimeId for any pre-existing AgentSession rows by
            // joining through Conversation.ProjectId → ProjectRuntime. We pick
            // the most-recently-created runtime per project as the best guess
            // when more than one exists (older sessions are historical anyway
            // and the queue/dispatch path won't touch them).
            //
            // For a greenfield DB this UPDATE is a no-op. If a row's project
            // has no ProjectRuntime, RuntimeId stays at Guid.Empty — that row
            // will then violate the FK below; in practice the only way to hit
            // that is local dev data, which we'd reset anyway. Documented here
            // so the failure mode is obvious if it ever fires.
            migrationBuilder.Sql(@"
                UPDATE ""AgentSessions"" s
                SET ""RuntimeId"" = sub.""RuntimeId""
                FROM (
                    SELECT s2.""Id"" AS ""SessionId"",
                           (SELECT pr.""Id""
                              FROM ""ProjectRuntimes"" pr
                              JOIN ""Conversations"" c ON c.""ProjectId"" = pr.""ProjectId""
                             WHERE c.""Id"" = s2.""ConversationId""
                             ORDER BY pr.""CreatedAt"" DESC
                             LIMIT 1) AS ""RuntimeId""
                    FROM ""AgentSessions"" s2
                ) sub
                WHERE s.""Id"" = sub.""SessionId""
                  AND sub.""RuntimeId"" IS NOT NULL;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_Runtime_Status_QueuePosition",
                table: "AgentSessions",
                columns: new[] { "RuntimeId", "Status", "QueuePosition" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_ProjectRuntimes_RuntimeId",
                table: "AgentSessions",
                column: "RuntimeId",
                principalTable: "ProjectRuntimes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_ProjectRuntimes_RuntimeId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_Runtime_Status_QueuePosition",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "QueuePosition",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "RuntimeId",
                table: "AgentSessions");
        }
    }
}
