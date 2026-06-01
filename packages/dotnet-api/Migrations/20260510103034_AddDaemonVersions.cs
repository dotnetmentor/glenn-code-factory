using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddDaemonVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DaemonVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BundleStorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    BundleSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BundleSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DaemonVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DaemonVersions_Channel_IsActive",
                table: "DaemonVersions",
                columns: new[] { "Channel", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_DaemonVersions_Channel_Version",
                table: "DaemonVersions",
                columns: new[] { "Channel", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DaemonVersions");
        }
    }
}
