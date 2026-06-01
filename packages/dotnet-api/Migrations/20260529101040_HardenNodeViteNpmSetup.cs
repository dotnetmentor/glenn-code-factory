using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Fixes a permanent bootstrap crash loop in the seeded <c>node-vite</c>
    /// ServicePreset that surfaced on managed runtimes whose persistent Fly
    /// volume carried a partial <c>node_modules</c> from a prior interrupted
    /// install.
    ///
    /// <para><b>Root cause — non-resilient <c>npm install</c>.</b> The original
    /// SetupContribution ran a bare
    /// <c>mise exec node -- npm install --no-audit --no-fund</c>. setup runs on
    /// every boot against the persistent volume; if a previous boot was
    /// interrupted mid-install, npm leaves behind atomic-rename temp dirs
    /// (e.g. <c>node_modules/.cesium-dGUGPXqg</c>). The next <c>npm install</c>
    /// then fails with <c>npm error code ENOTEMPTY ... rename
    /// node_modules/cesium -> node_modules/.cesium-dGUGPXqg</c>. setup never
    /// completes within the ~15-min bootstrap watchdog, the runtime is flagged
    /// Crashed and respawned, and it loops forever.</para>
    ///
    /// <para><b>Fix.</b> Make the npm step idempotent and immune to partial /
    /// corrupt <c>node_modules</c> on the volume: (1) sweep leftover
    /// <c>.&lt;pkg&gt;-XXXX</c> atomic-rename temp dirs before doing anything;
    /// (2) when a <c>package-lock.json</c> exists, use <c>npm ci</c> (which wipes
    /// <c>node_modules</c> first), with a fallback that hard-removes
    /// <c>node_modules</c> and retries once; (3) otherwise hard-remove
    /// <c>node_modules</c> and fall back to <c>npm install</c>. Every path tolerates
    /// a corrupt tree left by an interrupted prior boot.</para>
    ///
    /// <para>Idempotent data <c>UPDATE</c> by slug, mirroring
    /// <c>FixPostgres15Preset</c>. Down restores the original (crash-prone)
    /// <c>npm install</c> seed for faithful rollback. The old V3 seed migration
    /// is left untouched — fresh DBs replay it then this UPDATE, landing on the
    /// fixed value.</para>
    /// </summary>
    public partial class HardenNodeViteNpmSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""SetupContribution"" = E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n# Clear leftover atomic-rename temp dirs from a prior interrupted install\n# (npm leaves `.<pkg>-XXXX` dirs that cause ENOTEMPTY on the next install).\nfind node_modules -maxdepth 1 -type d -name ''.*-*'' -prune -exec rm -rf {} + 2>/dev/null || true\nif [ -f package-lock.json ]; then\n  /usr/local/bin/mise exec node@{{nodeVersion}} -- npm ci --no-audit --no-fund \\\n    || { rm -rf node_modules; /usr/local/bin/mise exec node@{{nodeVersion}} -- npm ci --no-audit --no-fund; }\nelse\n  rm -rf node_modules\n  /usr/local/bin/mise exec node@{{nodeVersion}} -- npm install --no-audit --no-fund\nfi',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'node-vite';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""SetupContribution"" = E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n/usr/local/bin/mise exec node@{{nodeVersion}} -- npm install --no-audit --no-fund',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'node-vite';");
        }
    }
}
