using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredDomainEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredDomainEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    EntityId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredDomainEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredDomainEvents_EntityType_EntityId",
                table: "StoredDomainEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredDomainEvents_EventType",
                table: "StoredDomainEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_StoredDomainEvents_OccurredAt",
                table: "StoredDomainEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredDomainEvents");
        }
    }
}
