using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <summary>
    /// One-off scrub of secrets that historical <c>BoxOperations.RequestPayload</c>
    /// rows persisted in plaintext. Until 2026-08-26, <c>BoxClient</c> stored the
    /// full fork/create/resume request body — including the runtime's
    /// <c>GLENN_RUNTIME_TOKEN</c> (a JWT) and <c>TUNNEL_TOKEN</c> in the
    /// <c>env</c> dictionary — and the env-refresh command string embedding the
    /// same values, all rendered verbatim by the admin Runtime Monitor drawer.
    /// New writes are masked at the source by <c>BoxAuditRedactor</c>; this
    /// migration retro-scrubs the rows written before that fix.
    ///
    /// <para>Three passes over the jsonb payload as text, narrowest first:
    /// (1) JSON string values of the known secret env keys, (2) shell
    /// <c>KEY='value'</c> assignments of those keys inside command strings,
    /// (3) anything JWT-shaped anywhere. The result is re-cast to jsonb — every
    /// replacement value is a plain <c>***</c>, so validity is preserved.</para>
    ///
    /// <para>Irreversible by design (<c>Down</c> is a no-op): the whole point is
    /// that the secrets no longer exist in the audit trail. The tokens
    /// themselves stay valid until natural expiry — rotation is operational
    /// concern, not schema concern.</para>
    /// </summary>
    public partial class RedactBoxOperationRequestSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "BoxOperations"
                SET "RequestPayload" = regexp_replace(
                        regexp_replace(
                            regexp_replace(
                                "RequestPayload"::text,
                                '("[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY|APIKEY)[A-Z0-9_]*"\s*:\s*")[^"]*(")',
                                '\1***\2', 'g'),
                            '([A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY|APIKEY)[A-Z0-9_]*=)''[^'']*''',
                            '\1''***''', 'g'),
                        'eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}',
                        '***', 'g')::jsonb
                WHERE "RequestPayload"::text ~ '(TOKEN|SECRET|PASSWORD|API_KEY|APIKEY|eyJ)';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: the scrub exists precisely so the plaintext secrets
            // are gone. Nothing to restore.
        }
    }
}
