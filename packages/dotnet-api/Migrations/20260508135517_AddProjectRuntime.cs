using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectRuntimes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StateChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FlyMachineId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FlyVolumeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Region = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VolumeSizeGb = table.Column<int>(type: "integer", nullable: false),
                    IdleThresholdMinutes = table.Column<int>(type: "integer", nullable: true),
                    RespawnRetries = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRuntimes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_FlyMachineId",
                table: "ProjectRuntimes",
                column: "FlyMachineId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_ProjectId",
                table: "ProjectRuntimes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_State",
                table: "ProjectRuntimes",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectRuntimes");
        }
    }
}
