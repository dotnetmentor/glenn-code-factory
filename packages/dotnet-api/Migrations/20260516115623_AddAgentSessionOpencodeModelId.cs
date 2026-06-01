using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionOpencodeModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OpencodeModelId",
                table: "AgentSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_OpencodeModelId",
                table: "AgentSessions",
                column: "OpencodeModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentSessions_OpencodeModels_OpencodeModelId",
                table: "AgentSessions",
                column: "OpencodeModelId",
                principalTable: "OpencodeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentSessions_OpencodeModels_OpencodeModelId",
                table: "AgentSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentSessions_OpencodeModelId",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "OpencodeModelId",
                table: "AgentSessions");
        }
    }
}
