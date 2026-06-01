using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationsAndSessionsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutoTitled",
                table: "Conversations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Sequence widens int -> long (Postgres integer -> bigint). The
            // initial AddConversationsAndSessions migration declared this as
            // integer; long-running sessions can emit tens of thousands of
            // events so bigint is the future-proof choice and the cost is
            // negligible. The composite PK on (SessionId, Sequence) is
            // automatically re-bound by Postgres when the column type changes.
            migrationBuilder.AlterColumn<long>(
                name: "Sequence",
                table: "AgentEvents",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            // "Archived filter" index — fast scan for the sidebar's "Show
            // archived" toggle and admin retention queries.
            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ArchivedAt",
                table: "Conversations",
                column: "ArchivedAt");

            // Sidebar list query: "newest activity first on (project, branch)".
            // EF Core 9 still doesn't expose per-column sort direction, so this
            // covering index is created via raw SQL. The original
            // AddConversationsAndSessions migration emitted the same index, but
            // AddProjectBranchEntity dropped Conversations.BranchId to retype
            // it from varchar -> uuid; Postgres cascades index drops with the
            // column, so the index is no longer present. Re-create it now that
            // the column shape is stable.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_Conversations_ProjectId_BranchId_LastActivityAt_DESC\" " +
                "ON \"Conversations\" (\"ProjectId\", \"BranchId\", \"LastActivityAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_Conversations_ProjectId_BranchId_LastActivityAt_DESC\";");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_ArchivedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "IsAutoTitled",
                table: "Conversations");

            migrationBuilder.AlterColumn<int>(
                name: "Sequence",
                table: "AgentEvents",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
