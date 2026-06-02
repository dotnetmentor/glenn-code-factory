using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// Adds <c>ruby-rails</c> so modern Ruby ships via runtime spec install (mise),
    /// not the base image. Idempotent INSERT — skips if slug already exists.
    /// </summary>
    public partial class AddRubyRailsServicePreset : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO ""ServicePresets"" (
    ""Id"", ""Slug"", ""DisplayName"", ""Description"", ""Category"", ""IconName"",
    ""IsBuiltIn"", ""CommandTemplate"", ""EnvTemplate"", ""HealthcheckCommand"",
    ""HealthcheckInterval"", ""DefaultUser"", ""Autorestart"",
    ""InstallContribution"", ""SetupContribution"", ""InstallVerify"",
    ""Parameters"", ""CreatedAt"", ""UpdatedAt"", ""IsDeleted""
)
SELECT
    gen_random_uuid(),
    'ruby-rails',
    'Ruby on Rails',
    'Rails app via mise Ruby (3.3+). Native build deps and bundle install run on first boot.',
    1,
    'Code',
    true,
    'bash -lc ''cd {{repoDir}}/{{project}} && exec /usr/local/bin/mise exec ruby@{{rubyVersion}} -- bundle exec rails server -b {{host}} -p {{port}}''',
    '{""MISE_DATA_DIR"":""/data/mise"",""HOME"":""/home/agent"",""RAILS_ENV"":""development""}'::jsonb,
    'curl -sf http://127.0.0.1:{{port}}/ >/dev/null 2>&1',
    5,
    'agent',
    true,
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\nDEBIAN_FRONTEND=noninteractive sudo apt-get update -y\nDEBIAN_FRONTEND=noninteractive sudo apt-get install -y --no-install-recommends \\\n  autoconf bison libssl-dev libyaml-dev libreadline-dev zlib1g-dev \\\n  libncurses-dev libffi-dev libgmp-dev libgdbm-dev libdb-dev uuid-dev \\\n  libpq-dev libxml2-dev libxslt1-dev libsqlite3-dev\n/usr/local/bin/mise install ruby@{{rubyVersion}}',
    E'set -euo pipefail\nexport MISE_DATA_DIR=/data/mise\ncd {{repoDir}}/{{project}}\n/usr/local/bin/mise exec ruby@{{rubyVersion}} -- bundle install --jobs 4',
    'command -v /usr/local/bin/mise',
    '[
        {""key"":""project"",""label"":""Project path"",""type"":""string"",""required"":true,""defaultValue"":"".""},
        {""key"":""rubyVersion"",""label"":""Ruby version"",""type"":""enum"",""required"":false,""defaultValue"":""3.3.7"",""enumOptions"":[""3.2.5"",""3.3.6"",""3.3.7""],""miseTool"":""ruby""},
        {""key"":""port"",""label"":""Port"",""type"":""integer"",""required"":false,""defaultValue"":""3000""},
        {""key"":""host"",""label"":""Bind host"",""type"":""string"",""required"":false,""defaultValue"":""0.0.0.0""}
    ]'::jsonb,
    NOW(),
    NOW(),
    false
WHERE NOT EXISTS (SELECT 1 FROM ""ServicePresets"" WHERE ""Slug"" = 'ruby-rails' AND ""IsDeleted"" = false);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE FROM ""ServicePresets"" WHERE ""Slug"" = 'ruby-rails';");
        }
    }
}
