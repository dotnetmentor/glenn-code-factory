using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SignatureId",
                table: "ErrorLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ErrorSignatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    IsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorSignatures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErrorLogs_SignatureId",
                table: "ErrorLogs",
                column: "SignatureId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorSignatures_Hash",
                table: "ErrorSignatures",
                column: "Hash",
                unique: true);

            // Composite dashboard index. EF Core 9 does not expose per-column sort
            // direction, so we emit the DESC explicitly via raw SQL. Postgres native
            // syntax; safe to drop + recreate in Down.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_ErrorSignatures_LastSeenAt_IsResolved\" " +
                "ON \"ErrorSignatures\" (\"LastSeenAt\" DESC, \"IsResolved\");");

            migrationBuilder.AddForeignKey(
                name: "FK_ErrorLogs_ErrorSignatures_SignatureId",
                table: "ErrorLogs",
                column: "SignatureId",
                principalTable: "ErrorSignatures",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ErrorLogs_ErrorSignatures_SignatureId",
                table: "ErrorLogs");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_ErrorSignatures_LastSeenAt_IsResolved\";");

            migrationBuilder.DropTable(
                name: "ErrorSignatures");

            migrationBuilder.DropIndex(
                name: "IX_ErrorLogs_SignatureId",
                table: "ErrorLogs");

            migrationBuilder.DropColumn(
                name: "SignatureId",
                table: "ErrorLogs");
        }
    }
}
