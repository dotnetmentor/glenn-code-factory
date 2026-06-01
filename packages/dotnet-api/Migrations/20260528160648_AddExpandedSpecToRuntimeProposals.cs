using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Adds the <c>ExpandedSpec</c> jsonb column on <c>RuntimeProposals</c>.
    ///
    /// <para><b>Why.</b> <c>ProposedSpec</c> is the V3 (preset-based) source
    /// of truth the user / agent authored. The daemon, however, still consumes
    /// V2 — <c>IPresetExpander.ExpandAsync</c> renders the V3 into V2 at
    /// propose-time. We persist that V2 here so the Approve / Edit path can
    /// compute the daemon-bound delta without re-expanding (avoiding drift if
    /// a preset row is edited between propose + approve).</para>
    ///
    /// <para><b>Nullable.</b> Legacy rows (pre-V3-cutover) have no expansion
    /// to backfill; the prior <c>AddServicePresetsV3</c> migration wiped the
    /// proposal table outright, so the only nullable case in practice is a
    /// future rejected proposal that short-circuits before reaching the
    /// expander.</para>
    /// </summary>
    public partial class AddExpandedSpecToRuntimeProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpandedSpec",
                table: "RuntimeProposals",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpandedSpec",
                table: "RuntimeProposals");
        }
    }
}
