using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddGithubIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GithubInstallations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<long>(type: "bigint", nullable: false),
                    AccountLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountAvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Suspended = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GithubInstallations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GithubInstallations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GithubUserIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    GithubUserId = table.Column<long>(type: "bigint", nullable: false),
                    GithubLogin = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GithubUserIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GithubUserIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GithubWebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Event = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GithubWebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GithubRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GithubInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GithubRepoId = table.Column<long>(type: "bigint", nullable: false),
                    Owner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Private = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GithubRepositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GithubRepositories_GithubInstallations_GithubInstallationId",
                        column: x => x.GithubInstallationId,
                        principalTable: "GithubInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GithubInstallations_InstallationId",
                table: "GithubInstallations",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GithubInstallations_WorkspaceId",
                table: "GithubInstallations",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_GithubRepositories_GithubInstallationId",
                table: "GithubRepositories",
                column: "GithubInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_GithubRepositories_GithubInstallationId_GithubRepoId",
                table: "GithubRepositories",
                columns: new[] { "GithubInstallationId", "GithubRepoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GithubUserIdentities_GithubUserId",
                table: "GithubUserIdentities",
                column: "GithubUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GithubUserIdentities_UserId",
                table: "GithubUserIdentities",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GithubWebhookDeliveries_DeliveryId",
                table: "GithubWebhookDeliveries",
                column: "DeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GithubWebhookDeliveries_Event_Action",
                table: "GithubWebhookDeliveries",
                columns: new[] { "Event", "Action" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GithubRepositories");

            migrationBuilder.DropTable(
                name: "GithubUserIdentities");

            migrationBuilder.DropTable(
                name: "GithubWebhookDeliveries");

            migrationBuilder.DropTable(
                name: "GithubInstallations");
        }
    }
}
