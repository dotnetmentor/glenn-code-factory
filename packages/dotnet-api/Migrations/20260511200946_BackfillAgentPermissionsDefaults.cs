using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Backfills the <c>Value</c> column for the five <c>AgentPermissions:*</c> rows
    /// in <c>SystemSettings</c> for environments where the row was inserted with an
    /// empty payload.
    ///
    /// <para>
    /// <b>Why this exists.</b> The original <c>SeedAgentPermissionsDefaults</c> migration
    /// (<c>20260511190000</c>) used <c>INSERT … ON CONFLICT (Key) DO NOTHING</c>. In
    /// environments where the runtime <c>SystemSettingsSeeder</c> hosted service had
    /// already booted once before the migration ran, the seeder inserted the catalog
    /// rows with <c>Value = NULL</c> (because no <c>IConfiguration</c> entry exists
    /// for the <c>AgentPermissions:*</c> keys). The migration's <c>ON CONFLICT</c> then
    /// no-op'd, leaving the rows present but unvalued — the API reports
    /// <c>hasValue: false</c> and the resolver falls through to "no policy".
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent.</b> Each UPDATE is guarded by <c>WHERE Value IS NULL OR Value = ''</c>,
    /// so this migration is safe to run any number of times and will never overwrite
    /// an operator-edited value. If the row is already populated (the happy path on
    /// fresh installs where the migration ran before the seeder) the UPDATE matches
    /// zero rows and exits cleanly.
    /// </para>
    ///
    /// <para>
    /// <b>Values mirrored from <c>AgentPermissionsDefaults</c></b> at
    /// <c>Source/Features/SystemSettings/SystemSettingsCatalog.cs</c>. Inlined here so
    /// the migration file is self-contained and reviewable without cross-referencing —
    /// the catalog holds the canonical copy.
    /// </para>
    /// </summary>
    public partial class BackfillAgentPermissionsDefaults : Migration
    {
        // Mirror of Source.Features.SystemSettings.AgentPermissionsDefaults.
        private const string PermissionMode = "bypassPermissions";
        private const string AllowDangerouslySkipPermissions = "true";
        private const string AllowedToolsJson = "[]";
        private const string AdditionalDirectoriesJson = "[]";

        // JSON array literal of the dangerous-shell denylist. Must match
        // AgentPermissionsDefaults.DisallowedToolsJson byte-for-byte so the resolver
        // sees the same payload whichever path stamps it.
        private const string DisallowedToolsJson =
            "[\"Bash(rm -rf /*)\"," +
            "\"Bash(rm -rf ~)\"," +
            "\"Bash(rm -rf .)\"," +
            "\"Bash(sudo *)\"," +
            "\"Bash(curl * | sh)\"," +
            "\"Bash(curl * | bash)\"," +
            "\"Bash(wget * | sh)\"," +
            "\"Bash(wget * | bash)\"," +
            "\"Bash(:(){ :|:& };:)\"," +
            "\"Bash(dd if=/dev/zero *)\"," +
            "\"Bash(mkfs.*)\"," +
            "\"Bash(> /dev/sda*)\"]";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            BackfillValue(
                migrationBuilder,
                key: "AgentPermissions:PermissionMode",
                value: PermissionMode);

            BackfillValue(
                migrationBuilder,
                key: "AgentPermissions:AllowDangerouslySkipPermissions",
                value: AllowDangerouslySkipPermissions);

            BackfillValue(
                migrationBuilder,
                key: "AgentPermissions:AllowedTools",
                value: AllowedToolsJson);

            BackfillValue(
                migrationBuilder,
                key: "AgentPermissions:DisallowedTools",
                value: DisallowedToolsJson);

            BackfillValue(
                migrationBuilder,
                key: "AgentPermissions:AdditionalDirectories",
                value: AdditionalDirectoriesJson);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse intent: clear the backfilled defaults back to NULL, but ONLY for
            // rows that still hold the exact shipped default. If an operator changed
            // the value after we backfilled, their value stays — the down migration
            // is meant to undo *this migration*, not an operator edit on top of it.
            ClearIfDefault(migrationBuilder, "AgentPermissions:PermissionMode", PermissionMode);
            ClearIfDefault(migrationBuilder, "AgentPermissions:AllowDangerouslySkipPermissions", AllowDangerouslySkipPermissions);
            ClearIfDefault(migrationBuilder, "AgentPermissions:AllowedTools", AllowedToolsJson);
            ClearIfDefault(migrationBuilder, "AgentPermissions:DisallowedTools", DisallowedToolsJson);
            ClearIfDefault(migrationBuilder, "AgentPermissions:AdditionalDirectories", AdditionalDirectoriesJson);
        }

        private static void BackfillValue(
            MigrationBuilder builder,
            string key,
            string value)
        {
            // Dollar-quoted PG literals avoid single-quote escaping on the JSON payloads.
            // Tag 'sql' is arbitrary; the payloads never contain "$sql$".
            // UpdatedAt bumped so cache invalidation / observability picks it up.
            builder.Sql($@"
                UPDATE ""SystemSettings""
                SET ""Value"" = $sql${value}$sql$,
                    ""UpdatedAt"" = (NOW() AT TIME ZONE 'UTC')
                WHERE ""Key"" = $sql${key}$sql$
                  AND (""Value"" IS NULL OR ""Value"" = '');
            ");
        }

        private static void ClearIfDefault(
            MigrationBuilder builder,
            string key,
            string defaultValue)
        {
            builder.Sql($@"
                UPDATE ""SystemSettings""
                SET ""Value"" = NULL,
                    ""UpdatedAt"" = (NOW() AT TIME ZONE 'UTC')
                WHERE ""Key"" = $sql${key}$sql$
                  AND ""Value"" = $sql${defaultValue}$sql$;
            ");
        }
    }
}
