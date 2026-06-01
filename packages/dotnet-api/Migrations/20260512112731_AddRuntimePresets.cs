using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimePresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimePresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IconUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ServiceSpec = table.Column<string>(type: "jsonb", nullable: false),
                    Popularity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimePresets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimePresets_Popularity_Name",
                table: "RuntimePresets",
                columns: new[] { "Popularity", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimePresets_Slug",
                table: "RuntimePresets",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimePresets");
        }
    }
}
