using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Adds the <c>SpecVersion</c> int column to <c>ProjectRuntimes</c>. Routes the
    /// jsonb in <c>Spec</c> between the legacy V1 shape
    /// (<c>{ languages, services, extras }</c>) and the V2 shape defined in
    /// <c>RuntimeSpecV2</c> (<c>{ install, services[], setup }</c>). Defaulted to
    /// <c>1</c> server-side so existing rows keep their old shape until the
    /// V1→V2 data migration (separate card) flips them.
    /// </summary>
    public partial class AddProjectRuntimeSpecVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpecVersion",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecVersion",
                table: "ProjectRuntimes");
        }
    }
}
