using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Adds the nullable jsonb <c>RequiredEnvContribution</c> column to
    /// <c>ServicePresets</c>. Stores a <c>List&lt;RequiredEnvVar&gt;</c> the
    /// preset's service declares it needs (key + optional description + secret
    /// hint). The <c>PresetExpander</c> merges this with any ad-hoc V3-declared
    /// vars into <c>ServiceSpec.RequiredEnv</c> so the UI can flag "required but
    /// not set". Nullable / no default — existing presets declare nothing and
    /// are unaffected; built-in requirement seeding is deferred to a later step.
    /// </summary>
    public partial class AddRequiredEnvContributionToServicePreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredEnvContribution",
                table: "ServicePresets",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredEnvContribution",
                table: "ServicePresets");
        }
    }
}
