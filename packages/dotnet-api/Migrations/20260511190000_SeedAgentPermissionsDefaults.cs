using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Seeds the "YOLO-with-guardrails" defaults for the new
    /// <c>AgentPermissions</c> category in the <c>SystemSettings</c> table.
    ///
    /// <para>
    /// The runtime <c>SystemSettingsSeeder</c> hosted service also stamps any
    /// missing catalog rows on every boot, so in steady state this migration is
    /// belt-and-braces. The reason it exists at all is to make the YOLO defaults
    /// land in a single, reviewable, append-only artifact: a fresh database
    /// running migrations gets the same baseline as a long-running database that
    /// already booted the seeder once. Subsequent boots and migrations both
    /// no-op cleanly thanks to <c>ON CONFLICT (Key) DO NOTHING</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent.</b> If an operator has already edited any of these rows
    /// (or the seeder ran first), the <c>ON CONFLICT</c> clause leaves their
    /// value alone. We never overwrite a non-default user-edited value.
    /// </para>
    ///
    /// <para>
    /// <b>Why raw SQL instead of <c>InsertData</c>:</b> <c>InsertData</c> emits
    /// a plain INSERT with no conflict handling, which would explode on the
    /// second-boot case where the seeder beat the migration. PostgreSQL's
    /// <c>ON CONFLICT</c> is the clean expression of "upsert that prefers the
    /// existing row" — see Source/Features/SystemSettings/SystemSettingsCatalog.cs
    /// for where the same constants live in code.
    /// </para>
    /// </summary>
    public partial class SeedAgentPermissionsDefaults : Migration
    {
        // Mirror of Source.Features.SystemSettings.AgentPermissionsDefaults.
        // Inlined here so the migration file is self-contained and reviewable
        // without cross-referencing — the catalog has the canonical copy and a
        // unit test should pin these in lockstep going forward.
        private const string PermissionMode = "bypassPermissions";
        private const string AllowDangerouslySkipPermissions = "true";
        private const string AllowedToolsJson = "[]";
        private const string AdditionalDirectoriesJson = "[]";

        // JSON array literal. No single quotes inside any pattern, so embedding
        // directly into a SQL string literal is safe — we still wrap with the
        // PG dollar-quoted form below to keep it visually unambiguous.
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

        private const string PermissionModeDescription =
            "How the SDK gates tool use. One of: 'default' (every Write/Edit/Bash " +
            "needs approval), 'acceptEdits' (auto-approve file edits, prompt for " +
            "Bash), 'bypassPermissions' (auto-approve everything except denylist), " +
            "'plan' (read-only planning turn), 'dontAsk' (never prompt — denials " +
            "from denylist still apply). Note: 'auto' is intentionally NOT offered.";

        private const string AllowDangerouslySkipPermissionsDescription =
            "Mirrors the SDK's --dangerously-skip-permissions flag. Required true " +
            "when PermissionMode is 'bypassPermissions'. Boolean stored as 'true' " +
            "or 'false'.";

        private const string AllowedToolsDescription =
            "JSON array of tool-pattern strings the agent may use without prompting " +
            "(e.g. 'Read', 'Bash(npm test)'). Empty array means 'no explicit allow-list'.";

        private const string DisallowedToolsDescription =
            "JSON array of tool-pattern strings the agent must never invoke. Wins " +
            "over bypassPermissions. The shipped default blocks the classic dangerous " +
            "shell patterns: rm -rf, sudo, curl-to-shell, fork bombs, raw-disk writes.";

        private const string AdditionalDirectoriesDescription =
            "JSON array of absolute paths the agent may read/write beyond its cwd. " +
            "Empty array means 'cwd only'.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each row: Key (PK), Category, Description, IsSecret=false, Value
            // (= the default), UpdatedBy=NULL (seeded, no operator edit yet),
            // CreatedAt/UpdatedAt=now UTC. ON CONFLICT keeps any already-present
            // row untouched — that's the idempotency contract.
            UpsertSetting(
                migrationBuilder,
                key: "AgentPermissions:PermissionMode",
                description: PermissionModeDescription,
                value: PermissionMode);

            UpsertSetting(
                migrationBuilder,
                key: "AgentPermissions:AllowDangerouslySkipPermissions",
                description: AllowDangerouslySkipPermissionsDescription,
                value: AllowDangerouslySkipPermissions);

            UpsertSetting(
                migrationBuilder,
                key: "AgentPermissions:AllowedTools",
                description: AllowedToolsDescription,
                value: AllowedToolsJson);

            UpsertSetting(
                migrationBuilder,
                key: "AgentPermissions:DisallowedTools",
                description: DisallowedToolsDescription,
                value: DisallowedToolsJson);

            UpsertSetting(
                migrationBuilder,
                key: "AgentPermissions:AdditionalDirectories",
                description: AdditionalDirectoriesDescription,
                value: AdditionalDirectoriesJson);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric rollback: remove every row this migration could have
            // inserted. If an operator edited a row after the fact we still
            // drop it — the catalog re-creates it on next boot via the seeder,
            // but the operator's edits are lost. That's acceptable for a
            // rollback path; the alternative (leaving stale rows) is worse
            // because re-running Up would then never restore the row to the
            // shipped default.
            migrationBuilder.Sql(@"
                DELETE FROM ""SystemSettings""
                WHERE ""Key"" IN (
                    'AgentPermissions:PermissionMode',
                    'AgentPermissions:AllowDangerouslySkipPermissions',
                    'AgentPermissions:AllowedTools',
                    'AgentPermissions:DisallowedTools',
                    'AgentPermissions:AdditionalDirectories'
                );
            ");
        }

        private static void UpsertSetting(
            MigrationBuilder builder,
            string key,
            string description,
            string value)
        {
            // Dollar-quoted PG string literals ($tag$...$tag$) keep us free of
            // single-quote escaping headaches for the JSON payloads. The tag
            // 'sql' is arbitrary — anything that doesn't appear in the content
            // works, and our payloads never contain "$sql$".
            builder.Sql($@"
                INSERT INTO ""SystemSettings""
                    (""Key"", ""Category"", ""Description"", ""IsSecret"",
                     ""Value"", ""UpdatedBy"", ""CreatedAt"", ""UpdatedAt"")
                VALUES
                    ($sql${key}$sql$,
                     $sql$AgentPermissions$sql$,
                     $sql${description}$sql$,
                     FALSE,
                     $sql${value}$sql$,
                     NULL,
                     (NOW() AT TIME ZONE 'UTC'),
                     (NOW() AT TIME ZONE 'UTC'))
                ON CONFLICT (""Key"") DO NOTHING;
            ");
        }
    }
}
