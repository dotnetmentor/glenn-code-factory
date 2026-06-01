using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeProposalTimingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhaseTimings",
                table: "RuntimeProposals",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalApplyMs",
                table: "RuntimeProposals",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhaseTimings",
                table: "RuntimeProposals");

            migrationBuilder.DropColumn(
                name: "TotalApplyMs",
                table: "RuntimeProposals");
        }
    }
}
