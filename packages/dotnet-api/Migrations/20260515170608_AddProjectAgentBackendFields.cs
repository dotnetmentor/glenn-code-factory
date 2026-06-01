using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAgentBackendFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentBackend",
                table: "Projects",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "claude");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedOpencodeZenApiKey",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpencodeModelId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OpencodeModelId",
                table: "Projects",
                column: "OpencodeModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_OpencodeModels_OpencodeModelId",
                table: "Projects",
                column: "OpencodeModelId",
                principalTable: "OpencodeModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_OpencodeModels_OpencodeModelId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_OpencodeModelId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AgentBackend",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EncryptedOpencodeZenApiKey",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OpencodeModelId",
                table: "Projects");
        }
    }
}
