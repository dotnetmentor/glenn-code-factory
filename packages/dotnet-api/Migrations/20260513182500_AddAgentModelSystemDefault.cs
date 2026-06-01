using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentModelSystemDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemDefault",
                table: "AgentModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Seed update — promote the existing sonnet row to system default.
            // Opus stays at the defaultValue:false from AddColumn above so no
            // explicit UpdateData is needed for it. The filtered unique index
            // is created AFTER this update so the half-second of two-rows-true
            // never happens.
            migrationBuilder.UpdateData(
                table: "AgentModels",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "IsSystemDefault",
                value: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentModels_OnlyOneSystemDefault",
                table: "AgentModels",
                column: "IsSystemDefault",
                unique: true,
                filter: "\"IsSystemDefault\" = true AND \"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentModels_OnlyOneSystemDefault",
                table: "AgentModels");

            migrationBuilder.DropColumn(
                name: "IsSystemDefault",
                table: "AgentModels");
        }
    }
}
