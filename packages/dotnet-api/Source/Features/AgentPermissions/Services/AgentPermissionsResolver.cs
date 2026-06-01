using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Source.Features.AgentPermissions.Models;
using Source.Features.SystemSettings;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.AgentPermissions.Services;

/// <summary>
/// Production implementation of <see cref="IAgentPermissionsResolver"/>. See the
/// interface for the contract; this class only owns the "how", not the "what".
///
/// <para><b>Why a single resolver instead of two readers + a merger?</b> The
/// spec is explicit that project overrides are complete (no merging). Keeping
/// the branch inside one resolver class makes that invariant impossible to
/// forget — there is no place where a partial merge could sneak in.</para>
///
/// <para><b>Why deserialize JSON inline rather than store typed values?</b>
/// The <c>SystemSettings</c> table is a single <c>(Key, Value)</c> shape where
/// every value is a string. Other categories follow the same pattern (e.g.
/// <c>RuntimeTokens</c> stores base64 keys as strings). Keeping list-typed
/// values as JSON strings preserves that uniformity — and matches the
/// migration that seeds the defaults, which writes
/// <see cref="AgentPermissionsDefaults.DisallowedToolsJson"/> verbatim.</para>
/// </summary>
public sealed class AgentPermissionsResolver : IAgentPermissionsResolver
{
    private readonly ApplicationDbContext _db;
    private readonly ISystemSettingsService _systemSettings;
    private readonly ILogger<AgentPermissionsResolver> _logger;

    public AgentPermissionsResolver(
        ApplicationDbContext db,
        ISystemSettingsService systemSettings,
        ILogger<AgentPermissionsResolver> logger)
    {
        _db = db;
        _systemSettings = systemSettings;
        _logger = logger;
    }

    public async Task<AgentPermissionsConfig> ResolveForProjectAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        // Step 1: presence-of-row IS the override (per the ProjectAgentPermissions
        // entity's lifecycle invariant). AsNoTracking — the resolver is read-only,
        // and a tracked snapshot would risk accidentally flushing alongside
        // unrelated writes in the same scope.
        var row = await _db.ProjectAgentPermissions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);

        if (row is not null)
        {
            return new AgentPermissionsConfig(
                PermissionMode: row.PermissionMode,
                AllowDangerouslySkipPermissions: row.AllowDangerouslySkipPermissions,
                AllowedTools: row.AllowedTools.AsReadOnly(),
                DisallowedTools: row.DisallowedTools.AsReadOnly(),
                AdditionalDirectories: row.AdditionalDirectories.AsReadOnly());
        }

        // Step 2: fall through to system defaults. ISystemSettingsService.Get reads
        // from the in-process cache (lazy-loads the AgentPermissions category on
        // first hit; subsequent turns are pure memory).
        var mode = _systemSettings.Get("AgentPermissions:PermissionMode")
                   ?? AgentPermissionsDefaults.PermissionMode;

        var skipRaw = _systemSettings.Get("AgentPermissions:AllowDangerouslySkipPermissions")
                      ?? AgentPermissionsDefaults.AllowDangerouslySkipPermissions;
        var skip = ParseBool(skipRaw);

        var allowed = DeserializeListOrDefault(
            _systemSettings.Get("AgentPermissions:AllowedTools"),
            fallback: Array.Empty<string>());

        var disallowed = DeserializeListOrDefault(
            _systemSettings.Get("AgentPermissions:DisallowedTools"),
            fallback: AgentPermissionsDefaults.DisallowedTools);

        var additional = DeserializeListOrDefault(
            _systemSettings.Get("AgentPermissions:AdditionalDirectories"),
            fallback: Array.Empty<string>());

        return new AgentPermissionsConfig(
            PermissionMode: mode,
            AllowDangerouslySkipPermissions: skip,
            AllowedTools: allowed,
            DisallowedTools: disallowed,
            AdditionalDirectories: additional);
    }

    /// <summary>
    /// Tolerant boolean parser. The catalog stores "true"/"false" but we accept
    /// the usual aliases so a hand-edited row doesn't fail closed in a way
    /// operators would never see at write time.
    /// </summary>
    private static bool ParseBool(string raw)
    {
        if (bool.TryParse(raw, out var b)) return b;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "yes" or "y" or "on" => true,
            "0" or "no" or "n" or "off" or "" => false,
            _ => false,
        };
    }

    /// <summary>
    /// Deserialise a JSON-array string into an immutable list, falling back to
    /// the supplied default on null/empty/malformed input. A malformed value is
    /// logged at warning level — it usually means a hand-edited
    /// <c>SystemSettings</c> row and operators want to know — but we don't
    /// throw because the daemon needs to keep running.
    /// </summary>
    private IReadOnlyList<string> DeserializeListOrDefault(string? raw, IReadOnlyList<string> fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw);
            return parsed is null ? fallback : parsed.AsReadOnly();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialise AgentPermissions list value (raw={Raw}); falling back to defaults.",
                raw);
            return fallback;
        }
    }
}
