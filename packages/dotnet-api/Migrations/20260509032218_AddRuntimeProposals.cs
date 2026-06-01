using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mutable runtime spec on ProjectRuntime — source of truth for what's
            // installed. Null on pre-curation runtimes; readers treat null as empty.
            migrationBuilder.AddColumn<string>(
                name: "Spec",
                table: "ProjectRuntimes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RuntimeProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProposedSpec = table.Column<string>(type: "jsonb", nullable: false),
                    AppliedSpec = table.Column<string>(type: "jsonb", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuntimeProposals_ProjectRuntimes_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "ProjectRuntimes",
                        principalColumn: "Id");
                });

            // FK lookup index — EF emits this for the RuntimeId FK; not used as
            // the primary read index (the DESC composite below covers that), but
            // EF requires it for the relationship.
            migrationBuilder.CreateIndex(
                name: "IX_RuntimeProposals_RuntimeId",
                table: "RuntimeProposals",
                column: "RuntimeId");

            // "Pending proposals" — small set, but the dashboard / approval
            // queue scans by Status frequently enough to warrant the index.
            migrationBuilder.CreateIndex(
                name: "IX_RuntimeProposals_Status",
                table: "RuntimeProposals",
                column: "Status");

            // "Proposal history for project X, latest first" — dominant read
            // pattern from the project detail page. CreatedAt sorted DESC so
            // range scans pick up newest rows first. EF Core 9 doesn't expose
            // per-column sort direction, so we emit via raw SQL. Mirrors the
            // FlyOperation / BootstrapRun / RuntimeStateEvent / SecretAuditEvent
            // / McpCalls precedent.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeProposals_ProjectId_CreatedAt\" " +
                "ON \"RuntimeProposals\" (\"ProjectId\", \"CreatedAt\" DESC);");

            // "Proposal history for runtime X, latest first" — confirmation
            // card / runtime detail view. DESC on CreatedAt for the same
            // reason as above.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeProposals_RuntimeId_CreatedAt\" " +
                "ON \"RuntimeProposals\" (\"RuntimeId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeProposals_RuntimeId_CreatedAt\";");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeProposals_ProjectId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "RuntimeProposals");

            migrationBuilder.DropColumn(
                name: "Spec",
                table: "ProjectRuntimes");
        }
    }
}
