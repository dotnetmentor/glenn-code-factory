using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredEntityChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredEntityChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Operation = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ChangedProperties = table.Column<string>(type: "jsonb", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredEntityChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredEntityChanges_EntityType",
                table: "StoredEntityChanges",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_StoredEntityChanges_EntityType_EntityId",
                table: "StoredEntityChanges",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredEntityChanges_OccurredAt",
                table: "StoredEntityChanges",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredEntityChanges");
        }
    }
}
