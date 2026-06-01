using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Destructive cutover for the <c>project-level-runtime-spec</c> spec.
    ///
    /// <para>Moves the runtime SERVICES spec out of <c>ProjectRuntimes</c>
    /// (where it was tracked per-runtime) and into <c>Projects</c> (one spec
    /// per project, all branches inherit). The fix retires the
    /// <c>OrderByDescending(CreatedAt).FirstOrDefault()</c> "newest runtime
    /// wins" lottery that caused approved specs to disappear from the Spec
    /// tab when a project had multiple branches.</para>
    ///
    /// <para><b>No data copy.</b> The legacy <c>ProjectRuntimes.Spec</c>
    /// column is dropped wholesale; existing rows are not migrated onto the
    /// new <c>Projects.Spec</c> column. Per the spec acceptance, the next
    /// agent approval / curation pass repopulates project specs from the
    /// ground truth (the agent's proposal flow) — re-populating from stale
    /// runtime rows would re-introduce the very ambiguity this refactor
    /// removes.</para>
    ///
    /// <para><b>Lazy convergence.</b> The daemon's bootstrap call now reads
    /// <c>Projects.Spec</c> on cold boot / wake / respawn, so live runtimes
    /// pick up the project's spec on their next reconnect without an
    /// explicit fan-out push.</para>
    /// </summary>
    public partial class MoveRuntimeSpecToProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Spec",
                table: "ProjectRuntimes");

            migrationBuilder.DropColumn(
                name: "SpecVersion",
                table: "ProjectRuntimes");

            migrationBuilder.AddColumn<string>(
                name: "Spec",
                table: "Projects",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecVersion",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Spec",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SpecVersion",
                table: "Projects");

            migrationBuilder.AddColumn<string>(
                name: "Spec",
                table: "ProjectRuntimes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecVersion",
                table: "ProjectRuntimes",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }
    }
}
