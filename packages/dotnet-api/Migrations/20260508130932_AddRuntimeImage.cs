using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Digest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Registry = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    GitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SizeMb = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeImages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeImages_Tag",
                table: "RuntimeImages",
                column: "Tag",
                unique: true);

            // "Latest active images" — BuiltAt sorted DESC so range scans pick up
            // newest rows first (default-spawn lookup). EF Core 9 doesn't expose
            // per-column sort direction, so we emit the index via raw SQL
            // (Postgres syntax). Same pattern as IX_FlyOperations_RuntimeId_CreatedAt.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_RuntimeImages_Status_BuiltAt\" " +
                "ON \"RuntimeImages\" (\"Status\", \"BuiltAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_RuntimeImages_Status_BuiltAt\";");

            migrationBuilder.DropTable(
                name: "RuntimeImages");
        }
    }
}
