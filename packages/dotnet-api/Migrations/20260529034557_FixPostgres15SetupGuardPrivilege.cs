using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Fixes a wake-up crash loop in the seeded <c>postgres-15</c> ServicePreset
    /// that surfaced the first time a runtime was stopped and resumed against a
    /// persisted Fly volume (runtime <c>525fd1fc</c>, 2026-05-29). The first boot
    /// succeeded end-to-end (initdb ran, postgres healthy, Online in 153s); the
    /// wake-up bootstrap then failed forever in <c>running-setup</c> with
    /// <c>initdb: error: directory "/data/project/services/postgres/data" exists
    /// but is not empty</c>.
    ///
    /// <para><b>Root cause — permission-blind guard.</b> The SetupContribution
    /// guarded initdb with <c>if [ ! -s {{dataDir}}/PG_VERSION ]</c>. The setup
    /// bash runs as the unprivileged <c>agent</c> user
    /// (<c>daemon/src/bootstrap/stages/RunningSetupStage.ts</c> — the executor
    /// binds to the daemon's uid, which is <c>agent</c>; that's also why the
    /// surrounding <c>mkdir</c>/<c>chown</c> lines need <c>sudo</c>). On first
    /// boot initdb creates the cluster and locks the data dir to
    /// <c>0700 postgres:postgres</c> — its mandatory permission mode. On every
    /// subsequent boot the <c>agent</c> user can no longer traverse into that
    /// directory to <c>stat</c> <c>PG_VERSION</c>, so <c>-s</c> evaluates false,
    /// <c>! false</c> is true, and the guard wrongly re-runs <c>initdb</c>
    /// against the now-populated persisted volume. initdb refuses a non-empty
    /// dir → stage fails <c>recoverable: true</c> → infinite retry loop. The
    /// volume is never harmed (initdb bails before writing), so this fails safe,
    /// but the runtime can never reach Online again.
    ///
    /// <para><b>Fix.</b> Run the existence check with the data dir owner's
    /// privilege — <c>sudo -u postgres test -s {{dataDir}}/PG_VERSION</c> — so it
    /// can actually read the persisted <c>0700</c> cluster. The <c>agent</c>
    /// account has passwordless sudo in the runtime base image
    /// (<c>Dockerfile.runtime-base:209-212</c>), the same mechanism the
    /// <c>mkdir</c>/<c>chown</c>/<c>initdb</c> lines already rely on.
    /// <list type="bullet">
    ///   <item>First boot: dir/PG_VERSION absent → <c>test -s</c> false →
    ///         <c>! false</c> true → initdb runs (correct).</item>
    ///   <item>Wake-up: postgres reads its own cluster, PG_VERSION non-empty →
    ///         <c>test -s</c> true → <c>! true</c> false → initdb skipped
    ///         (correct).</item>
    /// </list>
    /// This is the only change — <c>mkdir</c>/<c>chown</c>/<c>initdb</c> already
    /// carry the correct sudo from <c>FixPostgres15Preset</c>.</para></para>
    ///
    /// <para>Idempotent data <c>UPDATE</c> by slug. Down restores the
    /// permission-blind guard from <c>FixPostgres15Preset</c> for faithful
    /// rollback.</para>
    /// </summary>
    public partial class FixPostgres15SetupGuardPrivilege : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""SetupContribution"" = E'set -euo pipefail\nif ! sudo -u postgres test -s {{dataDir}}/PG_VERSION; then\n  sudo mkdir -p {{dataDir}}\n  sudo chown -R postgres:postgres {{dataDir}}\n  sudo -u postgres /usr/lib/postgresql/15/bin/initdb -D {{dataDir}}\nfi',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'postgres-15';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""SetupContribution"" = E'set -euo pipefail\nif [ ! -s {{dataDir}}/PG_VERSION ]; then\n  sudo mkdir -p {{dataDir}}\n  sudo chown -R postgres:postgres {{dataDir}}\n  sudo -u postgres /usr/lib/postgresql/15/bin/initdb -D {{dataDir}}\nfi',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'postgres-15';");
        }
    }
}
