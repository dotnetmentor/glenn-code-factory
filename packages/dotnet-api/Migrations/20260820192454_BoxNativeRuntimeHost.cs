using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class BoxNativeRuntimeHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlyOperations");

            migrationBuilder.DropTable(
                name: "RuntimeImages");

            migrationBuilder.DropIndex(
                name: "IX_ProjectRuntimes_FlyMachineId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "FlyMachineId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "FlyVolumeId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "ImageDigest",
                table: "ProjectRuntimes");

            migrationBuilder.AddColumn<string>(
                name: "BoxId",
                table: "ProjectRuntimes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateBoxId",
                table: "ProjectRuntimes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BoxOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ResponsePayload = table.Column<string>(type: "jsonb", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoxOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoxId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_BoxId",
                table: "ProjectRuntimes",
                column: "BoxId");

            migrationBuilder.CreateIndex(
                name: "IX_BoxOperations_RequestKey",
                table: "BoxOperations",
                column: "RequestKey");

            migrationBuilder.CreateIndex(
                name: "IX_BoxOperations_RuntimeId_CreatedAt",
                table: "BoxOperations",
                columns: new[] { "RuntimeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeTemplates_BoxId",
                table: "RuntimeTemplates",
                column: "BoxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeTemplates_Status_BuiltAt",
                table: "RuntimeTemplates",
                columns: new[] { "Status", "BuiltAt" });

            // Data fix for the Fly → Box cutover: every pre-existing runtime row
            // referenced a Fly machine that no longer exists (the Fly account was
            // torn down). Rows left Pending will provision fresh on Box on the
            // next provisioner tick; everything else is walked to Failed so the
            // user's Restart / Reset-from-scratch buttons take the normal
            // re-provision path instead of the reconciler thrashing on ghosts.
            migrationBuilder.Sql("""
                UPDATE "ProjectRuntimes"
                SET "State" = 'Failed',
                    "StateChangedAt" = now() at time zone 'utc'
                WHERE "State" NOT IN ('Pending', 'Deleted', 'Failed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoxOperations");

            migrationBuilder.DropTable(
                name: "RuntimeTemplates");

            migrationBuilder.DropIndex(
                name: "IX_ProjectRuntimes_BoxId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "BoxId",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "TemplateBoxId",
                table: "ProjectRuntimes");

            migrationBuilder.AddColumn<string>(
                name: "FlyMachineId",
                table: "ProjectRuntimes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlyVolumeId",
                table: "ProjectRuntimes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageDigest",
                table: "ProjectRuntimes",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FlyOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ResponsePayload = table.Column<string>(type: "jsonb", nullable: true),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlyOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Digest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    GitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Registry = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SizeMb = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeImages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRuntimes_FlyMachineId",
                table: "ProjectRuntimes",
                column: "FlyMachineId");

            migrationBuilder.CreateIndex(
                name: "IX_FlyOperations_RequestKey",
                table: "FlyOperations",
                column: "RequestKey");

            migrationBuilder.CreateIndex(
                name: "IX_FlyOperations_RuntimeId_CreatedAt",
                table: "FlyOperations",
                columns: new[] { "RuntimeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeImages_Status_BuiltAt",
                table: "RuntimeImages",
                columns: new[] { "Status", "BuiltAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeImages_Tag",
                table: "RuntimeImages",
                column: "Tag",
                unique: true);
        }
    }
}
