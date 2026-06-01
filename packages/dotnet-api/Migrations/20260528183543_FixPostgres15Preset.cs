using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Fixes three latent bugs in the seeded <c>postgres-15</c> ServicePreset
    /// that surfaced once the V3 E2E flow actually applied the preset on a real
    /// runtime VM (project <c>6fb7e1ea</c>, runtime <c>912b9fb0</c>, 2026-05-28):
    ///
    /// <list type="number">
    ///   <item>
    ///     <b>supervisord setuid failure (FATAL crash loop)</b> — The original
    ///     preset set <c>DefaultUser = 'postgres'</c>, which causes the daemon's
    ///     <c>SupervisordController.renderServiceBlock()</c> to emit
    ///     <c>user=postgres</c> in the program block. But supervisord itself runs
    ///     as <c>agent</c> (see <c>docker/supervisord.base.conf</c>), and a
    ///     non-root supervisord cannot <c>setuid()</c> to another user. The
    ///     observed runtime error was
    ///     <c>supervisor: couldn't setuid to 100: Can't drop privilege as nonroot user</c>
    ///     followed by <c>supervisor: child process was not spawned</c>.
    ///     <para>Fix: keep supervisord running as <c>agent</c>
    ///     (<c>DefaultUser = 'agent'</c>) and wrap the command in
    ///     <c>sudo -u postgres exec ...</c> so the privilege drop happens through
    ///     setuid-root <c>sudo</c> instead of non-root supervisord. The agent
    ///     account has passwordless sudo in the runtime base image
    ///     (<c>Dockerfile.runtime-base:209-212</c>).</para>
    ///   </item>
    ///   <item>
    ///     <b>InstallContribution apt-get permission denied</b> — The original
    ///     ran <c>apt-get install -y postgresql-15</c> with no <c>sudo</c>.
    ///     Install scripts execute as the <c>agent</c> user
    ///     (<c>daemon/src/bootstrap/stages/InstallStage.ts:40-49</c>), so apt
    ///     couldn't take the dpkg lock. The observed error in proposal
    ///     <c>eadb3806</c> was <c>E: Could not open lock file
    ///     /var/lib/apt/lists/lock</c>.
    ///     <para>Fix: prefix <c>sudo</c> AND add a presence guard so the install
    ///     short-circuits when postgres-15 is pre-baked in the runtime image
    ///     (which it currently is — <c>Dockerfile.runtime-base:124-126</c>).</para>
    ///   </item>
    ///   <item>
    ///     <b>SetupContribution mkdir/chown permission denied</b> — The original
    ///     <c>mkdir -p {{dataDir}}</c> and <c>chown -R postgres:postgres {{dataDir}}</c>
    ///     also ran without <c>sudo</c>, and the default <c>dataDir</c> is
    ///     <c>/data/postgres</c> which is owned by root in the base image.
    ///     <para>Fix: prefix both with <c>sudo</c>. The subsequent
    ///     <c>sudo -u postgres initdb</c> was already correct.</para>
    ///   </item>
    /// </list>
    ///
    /// <para>Idempotent: a plain <c>UPDATE</c> by slug — re-running has no effect
    /// past the first apply because the WHERE clause never re-matches changed
    /// rows. Down migration restores the original (broken) seed values for
    /// faithful rollback.</para>
    /// </summary>
    public partial class FixPostgres15Preset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""CommandTemplate"" = 'bash -lc ''exec sudo -u postgres /usr/lib/postgresql/15/bin/postgres -D {{dataDir}} -p {{port}}''',
    ""DefaultUser"" = 'agent',
    ""InstallContribution"" = E'set -euo pipefail\nif ! command -v /usr/lib/postgresql/15/bin/postgres >/dev/null 2>&1; then\n  DEBIAN_FRONTEND=noninteractive sudo apt-get update -y\n  DEBIAN_FRONTEND=noninteractive sudo apt-get install -y postgresql-15\nfi',
    ""SetupContribution"" = E'set -euo pipefail\nif [ ! -s {{dataDir}}/PG_VERSION ]; then\n  sudo mkdir -p {{dataDir}}\n  sudo chown -R postgres:postgres {{dataDir}}\n  sudo -u postgres /usr/lib/postgresql/15/bin/initdb -D {{dataDir}}\nfi',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'postgres-15';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE ""ServicePresets""
SET
    ""CommandTemplate"" = 'bash -lc ''exec /usr/lib/postgresql/15/bin/postgres -D {{dataDir}} -p {{port}}''',
    ""DefaultUser"" = 'postgres',
    ""InstallContribution"" = E'set -euo pipefail\nDEBIAN_FRONTEND=noninteractive apt-get update -y\nDEBIAN_FRONTEND=noninteractive apt-get install -y postgresql-15',
    ""SetupContribution"" = E'set -euo pipefail\nif [ ! -s {{dataDir}}/PG_VERSION ]; then\n  mkdir -p {{dataDir}}\n  chown -R postgres:postgres {{dataDir}}\n  sudo -u postgres /usr/lib/postgresql/15/bin/initdb -D {{dataDir}}\nfi',
    ""UpdatedAt"" = NOW()
WHERE ""Slug"" = 'postgres-15';");
        }
    }
}
