using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectKeyMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WrappedDek = table.Column<byte[]>(type: "bytea", nullable: false),
                    MasterKeyVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectKeyMaterials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    DekVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSecrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSecrets_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecretAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecretKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Actor = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectKeyMaterials_ProjectId",
                table: "ProjectKeyMaterials",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecrets_CreatedBy",
                table: "ProjectSecrets",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSecrets_ProjectId_Key",
                table: "ProjectSecrets",
                columns: new[] { "ProjectId", "Key" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            // "Audit trail for project X, latest first" — CreatedAt sorted DESC so
            // range scans pick up newest rows first. EF Core 9 doesn't expose
            // per-column sort direction, so we emit the index via raw SQL (Postgres
            // syntax). Mirrors the FlyOperation / BootstrapRun / RuntimeStateEvent
            // precedent.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_SecretAuditEvents_ProjectId_CreatedAt\" " +
                "ON \"SecretAuditEvents\" (\"ProjectId\", \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_SecretAuditEvents_ProjectId_CreatedAt\";");

            migrationBuilder.DropTable(
                name: "ProjectKeyMaterials");

            migrationBuilder.DropTable(
                name: "ProjectSecrets");

            migrationBuilder.DropTable(
                name: "SecretAuditEvents");
        }
    }
}
