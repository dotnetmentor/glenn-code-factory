using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IconKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceRepoOwner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceRepoName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RuntimeSpec = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TemplateId",
                table: "Projects",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_IsActive_SortOrder",
                table: "ProjectTemplates",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_Slug",
                table: "ProjectTemplates",
                column: "Slug",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_ProjectTemplates_TemplateId",
                table: "Projects",
                column: "TemplateId",
                principalTable: "ProjectTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_ProjectTemplates_TemplateId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "ProjectTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Projects_TemplateId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "Projects");
        }
    }
}
