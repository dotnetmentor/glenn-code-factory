using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Destructive cutover to the Runtime Spec V3 preset registry.
    ///
    /// <para><b>What this migration does, in order:</b>
    /// <list type="number">
    ///   <item>Wipes <c>Projects.Spec</c> content (column stays — V3 will
    ///         repopulate it with the new shape).</item>
    ///   <item>Deletes every <c>RuntimeProposals</c> row (these are agent
    ///         drafts, not user data — the spec explicitly authorises the
    ///         drop and the next proposal pass repopulates).</item>
    ///   <item>Drops the legacy V2 <c>RuntimePresets</c> table outright.</item>
    ///   <item>Creates the V3 <c>ServicePresets</c> table.</item>
    ///   <item>Seeds 5 built-in presets (dotnet-mise, node-vite, node-script,
    ///         postgres-15, bash-raw) as raw INSERTs with hardcoded UUIDs so
    ///         the rows are referenceable from other migrations / tests.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Why no data migration.</b> V2's <c>RuntimePreset</c> shape
    /// (single ServiceSpec per row, no templating, no parameter schema) maps
    /// onto NOTHING in V3 — every column is gone or renamed. Translating
    /// would leave a half-V2/half-V3 table that's harder to reason about than
    /// a clean cut. The spec authorises the wipe; users haven't accepted any
    /// V2 proposals into production specs at the time of this migration.</para>
    ///
    /// <para><b>Down() is a stub.</b> Recreates an empty <c>RuntimePresets</c>
    /// table for schema-symmetry but doesn't restore the V2 seed data —
    /// re-seeding via raw SQL across two providers (Postgres dev, EF InMemory
    /// tests) is more trouble than it's worth, and the project's deploy story
    /// is forward-only. If a rollback ever happens in anger an operator can
    /// re-run the deleted <c>RuntimePresetSeeder</c> by hand.</para>
    /// </summary>
    public partial class AddServicePresetsV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Wipe legacy V2 spec / proposal data ----
            // Projects.Spec content (V2 shape) is incompatible with V3.
            // The column stays — V3 proposals will repopulate.
            migrationBuilder.Sql("UPDATE \"Projects\" SET \"Spec\" = NULL;");

            // Agent drafts only — no FKs in or out, no user data. Whole-table delete.
            migrationBuilder.Sql("DELETE FROM \"RuntimeProposals\";");

            // ---- 2. Drop the V2 preset table ----
            migrationBuilder.DropTable(
                name: "RuntimePresets");

            // ---- 3. Create the V3 ServicePresets table ----
            migrationBuilder.CreateTable(
                name: "ServicePresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    IconName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    CommandTemplate = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    EnvTemplate = table.Column<string>(type: "jsonb", nullable: false),
                    HealthcheckCommand = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    HealthcheckInterval = table.Column<int>(type: "integer", nullable: true),
                    DefaultUser = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Autorestart = table.Column<bool>(type: "boolean", nullable: false),
                    InstallContribution = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    SetupContribution = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    InstallVerify = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePresets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePresets_Category_DisplayName",
                table: "ServicePresets",
                columns: new[] { "Category", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePresets_Slug",
                table: "ServicePresets",
                column: "Slug",
                unique: true);

            // ---- 4. Seed the 5 built-in presets ----
            SeedBuiltInPresets(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServicePresets");

            // Schema-only recreate of the V2 table — no seed data restored.
            // The Projects.Spec wipe is not reversible by design.
            migrationBuilder.CreateTable(
                name: "RuntimePresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IconUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Popularity = table.Column<int>(type: "integer", nullable: false),
                    ServiceSpec = table.Column<string>(type: "jsonb", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimePresets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimePresets_Popularity_Name",
                table: "RuntimePresets",
                columns: new[] { "Popularity", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimePresets_Slug",
                table: "RuntimePresets",
                column: "Slug",
                unique: true);
        }

        /// <summary>
        /// Inserts the 5 built-in presets in a single SQL batch. Categories
        /// are persisted as the int values pinned in <c>PresetCategory</c>:
        /// Backend=0, Frontend=1, Database=2, Worker=3, Other=4.
        ///
        /// <para>Parameter <c>type</c> values inside the jsonb blobs use the
        /// lowerCamelCase enum names (<c>"string"</c>, <c>"integer"</c>,
        /// <c>"boolean"</c>, <c>"enum"</c>) — matches the JsonStringEnumConverter
        /// configured in <see cref="Source.Features.RuntimePresets.Models.PresetParameter.JsonOptions"/>
        /// so the row round-trips cleanly through the C# reader.</para>
        /// </summary>
        private static void SeedBuiltInPresets(MigrationBuilder migrationBuilder)
        {
            // 1. dotnet-mise — .NET via mise toolchain.
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
) VALUES (
    gen_random_uuid(),
    'dotnet-mise',
    '.NET (mise toolchain)',
    'Run a .NET app via the mise toolchain. Build is in setup (avoids cold-start healthcheck timeout).',
    0,
    'Code',
    true,
    'bash -lc ''cd {{repoDir}}/{{project}} && exec /usr/local/bin/mise exec dotnet@{{dotnetVersion}} -- dotnet run --no-build --no-launch-profile --urls http://0.0.0.0:{{port}}''',
    '{""MISE_DATA_DIR"":""/data/mise"",""HOME"":""/home/agent"",""ASPNETCORE_ENVIRONMENT"":""Development"",""ASPNETCORE_URLS"":""http://0.0.0.0:{{port}}""}'::jsonb,
    'curl -sf http://127.0.0.1:{{port}}{{healthcheckPath}} >/dev/null 2>&1',
    5,
    'agent',
    true,
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\n/usr/local/bin/mise install dotnet@{{dotnetVersion}}',
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n/usr/local/bin/mise exec dotnet@{{dotnetVersion}} -- dotnet restore\n/usr/local/bin/mise exec dotnet@{{dotnetVersion}} -- dotnet build --no-restore -c Debug',
    'command -v /usr/local/bin/mise',
    '[
        {""key"":""project"",""label"":""Project path"",""type"":""string"",""required"":true,""description"":""Path under repoDir, e.g. packages/dotnet-api""},
        {""key"":""dotnetVersion"",""label"":"".NET version"",""type"":""enum"",""required"":false,""defaultValue"":""9"",""enumOptions"":[""7"",""8"",""9""],""miseTool"":""dotnet""},
        {""key"":""port"",""label"":""Port"",""type"":""integer"",""required"":false,""defaultValue"":""5338""},
        {""key"":""healthcheckPath"",""label"":""Healthcheck path"",""type"":""string"",""required"":false,""defaultValue"":""/health""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
);");

            // 2. node-vite — Vite dev server.
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
) VALUES (
    gen_random_uuid(),
    'node-vite',
    'Node + Vite dev server',
    'Run a Vite dev server. npm install is in setup. Healthcheck hits root.',
    1,
    'Web',
    true,
    'bash -lc ''cd {{repoDir}}/{{project}} && exec /usr/local/bin/mise exec node@{{nodeVersion}} -- npm run dev -- --host {{host}} --port {{port}}''',
    '{""MISE_DATA_DIR"":""/data/mise"",""HOME"":""/home/agent"",""NODE_ENV"":""development""}'::jsonb,
    'curl -sf http://127.0.0.1:{{port}}/ >/dev/null 2>&1',
    5,
    'agent',
    true,
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\n/usr/local/bin/mise install node@{{nodeVersion}}',
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n/usr/local/bin/mise exec node@{{nodeVersion}} -- npm install --no-audit --no-fund',
    'command -v /usr/local/bin/mise',
    '[
        {""key"":""project"",""label"":""Project path"",""type"":""string"",""required"":true},
        {""key"":""nodeVersion"",""label"":""Node version"",""type"":""enum"",""required"":false,""defaultValue"":""20"",""enumOptions"":[""18"",""20"",""22""],""miseTool"":""node""},
        {""key"":""port"",""label"":""Port"",""type"":""integer"",""required"":false,""defaultValue"":""5173""},
        {""key"":""host"",""label"":""Host"",""type"":""string"",""required"":false,""defaultValue"":""0.0.0.0""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
);");

            // 3. node-script — generic Node app via npm run <script>.
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
) VALUES (
    gen_random_uuid(),
    'node-script',
    'Node app (npm script)',
    'Generic Node service running an npm script (e.g. start, dev).',
    0,
    'Terminal',
    true,
    'bash -lc ''cd {{repoDir}}/{{project}} && exec /usr/local/bin/mise exec node@{{nodeVersion}} -- npm run {{script}}''',
    '{""MISE_DATA_DIR"":""/data/mise"",""HOME"":""/home/agent"",""NODE_ENV"":""development"",""PORT"":""{{port}}""}'::jsonb,
    'curl -sf http://127.0.0.1:{{port}}{{healthcheckPath}} >/dev/null 2>&1',
    5,
    'agent',
    true,
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\n/usr/local/bin/mise install node@{{nodeVersion}}',
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n/usr/local/bin/mise exec node@{{nodeVersion}} -- npm install --no-audit --no-fund',
    'command -v /usr/local/bin/mise',
    '[
        {""key"":""project"",""label"":""Project path"",""type"":""string"",""required"":true},
        {""key"":""nodeVersion"",""label"":""Node version"",""type"":""enum"",""required"":false,""defaultValue"":""20"",""enumOptions"":[""18"",""20"",""22""],""miseTool"":""node""},
        {""key"":""script"",""label"":""npm script"",""type"":""string"",""required"":true,""defaultValue"":""start""},
        {""key"":""port"",""label"":""Port"",""type"":""integer"",""required"":false,""defaultValue"":""3000""},
        {""key"":""healthcheckPath"",""label"":""Healthcheck path"",""type"":""string"",""required"":false,""defaultValue"":""/""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
);");

            // 4. postgres-15 — Postgres 15 with initdb on first boot.
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
) VALUES (
    gen_random_uuid(),
    'postgres-15',
    'PostgreSQL 15',
    'Postgres 15 with initdb on first boot. Data persists at dataDir.',
    2,
    'Storage',
    true,
    'bash -lc ''exec /usr/lib/postgresql/15/bin/postgres -D {{dataDir}} -p {{port}}''',
    '{""HOME"":""/home/agent"",""PGDATA"":""{{dataDir}}""}'::jsonb,
    '/usr/lib/postgresql/15/bin/pg_isready -h 127.0.0.1 -p {{port}} >/dev/null 2>&1',
    5,
    'postgres',
    true,
    E'set -euo pipefail\nDEBIAN_FRONTEND=noninteractive apt-get update -y\nDEBIAN_FRONTEND=noninteractive apt-get install -y postgresql-15',
    E'set -euo pipefail\nif [ ! -s {{dataDir}}/PG_VERSION ]; then\n  mkdir -p {{dataDir}}\n  chown -R postgres:postgres {{dataDir}}\n  sudo -u postgres /usr/lib/postgresql/15/bin/initdb -D {{dataDir}}\nfi',
    'command -v /usr/lib/postgresql/15/bin/postgres',
    '[
        {""key"":""dataDir"",""label"":""Data directory"",""type"":""string"",""required"":false,""defaultValue"":""/data/postgres""},
        {""key"":""port"",""label"":""Port"",""type"":""integer"",""required"":false,""defaultValue"":""5432""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
);");

            // 5. bash-raw — escape hatch for advanced custom services.
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
) VALUES (
    gen_random_uuid(),
    'bash-raw',
    'Custom bash (advanced)',
    'Escape hatch — full freeform bash. Use only when no other preset fits.',
    4,
    'Code',
    true,
    '{{command}}',
    '{""HOME"":""/home/agent""}'::jsonb,
    '{{healthcheckCmd}}',
    5,
    'agent',
    true,
    '{{install}}',
    '{{setup}}',
    NULL,
    '[
        {""key"":""command"",""label"":""Command"",""type"":""string"",""required"":true},
        {""key"":""healthcheckCmd"",""label"":""Healthcheck command"",""type"":""string"",""required"":false,""defaultValue"":""""},
        {""key"":""install"",""label"":""Install script"",""type"":""string"",""required"":false,""defaultValue"":""""},
        {""key"":""setup"",""label"":""Setup script"",""type"":""string"",""required"":false,""defaultValue"":""""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
);");
        }
    }
}
