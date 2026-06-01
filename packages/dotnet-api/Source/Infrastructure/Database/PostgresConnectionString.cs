using Npgsql;

namespace Source.Infrastructure.Database;

/// <summary>
/// Resolves and normalizes the Postgres connection string used by both EF Core and Hangfire.
///
/// <para>Managed hosts (Render, Heroku, Railway, …) expose the database as a
/// <c>DATABASE_URL</c> in <b>URI</b> form — <c>postgres://user:pass@host:port/db?sslmode=require</c>.
/// Npgsql only understands the <b>keyword</b> form (<c>Host=…;Port=…;Database=…;Username=…;Password=…</c>),
/// so handing a URI straight to <see cref="NpgsqlDataSourceBuilder"/> throws at startup. This helper
/// detects the URI form and rewrites it; anything already in keyword form is returned verbatim so
/// local development and explicitly-configured strings are untouched.</para>
///
/// <para>For the URI form we also enforce TLS (<c>SSL Mode=Require</c>) unless the URI's
/// <c>sslmode</c> query parameter says otherwise. In Npgsql 9 <c>Require</c> encrypts the
/// connection without validating the certificate chain — exactly what a managed provider's
/// provider-managed certificate needs, with no CA bundle to ship.</para>
/// </summary>
public static class PostgresConnectionString
{
    /// <summary>
    /// Resolve from <c>DATABASE_URL</c> (preferred, set by the deployment host) or the
    /// <c>DefaultConnection</c> config entry, then normalize to Npgsql keyword form.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var raw = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No database connection string found. Set the DATABASE_URL environment variable or ConnectionStrings:DefaultConnection.");

        return Normalize(raw);
    }

    /// <summary>
    /// Convert a <c>postgres://</c>/<c>postgresql://</c> URI to Npgsql keyword form.
    /// A string that is already in keyword form is returned unchanged.
    /// </summary>
    public static string Normalize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Database connection string is empty.");
        }

        var trimmed = connectionString.Trim();

        var isUri = trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        return isUri ? ConvertUriToKeyword(trimmed) : connectionString;
    }

    private static string ConvertUriToKeyword(string uri)
    {
        var parsed = new Uri(uri);

        var userInfo = parsed.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = parsed.Host,
            Port = parsed.Port > 0 ? parsed.Port : 5432,
            Database = parsed.AbsolutePath.TrimStart('/'),
            Username = username,
            Password = password,
        };

        // Honor an explicit sslmode query parameter; otherwise require TLS, which managed
        // Postgres providers expect. In Npgsql 9, SslMode.Require encrypts without validating
        // the certificate chain, so no CA bundle or TrustServerCertificate flag is needed.
        var sslmode = GetQueryValue(parsed.Query, "sslmode") ?? "require";
        builder.SslMode = MapSslMode(sslmode);

        return builder.ConnectionString;
    }

    private static SslMode MapSslMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => SslMode.Require,
    };

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }
}
