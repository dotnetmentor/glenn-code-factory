using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Source.Features.SystemSettings.Services;

namespace Source.Features.RuntimeTokens.Services;

/// <summary>
/// Source of truth for RuntimeToken HMAC-SHA256 signing material.
///
/// <para>Keys live in <see cref="ISystemSettingsService"/> under the
/// <c>RuntimeTokens</c> category — never in <c>appsettings.json</c>, never in
/// <c>IConfiguration</c>. On the very first read, if no Current key is present,
/// the service generates a 32-byte random key, persists it via
/// <c>SetAsync(..., isSecret: true)</c>, and uses it for the rest of the process'
/// lifetime. Operators can later rotate by editing the SystemSettings admin UI;
/// the cache here drops on the next <see cref="SystemSettingChanged"/> for the
/// <c>RuntimeTokens</c> category.</para>
///
/// <para>Two-key validation set: signing always uses Current; validation accepts
/// Current and (when set) Previous. The "Previous" slot is the rotation window —
/// once it's cleared, only Current remains.</para>
/// </summary>
public interface IRuntimeTokenSigningKeyService
{
    /// <summary>
    /// The signing credentials used to sign newly-minted tokens. Wraps the
    /// "Current" key — never the "Previous" key.
    /// </summary>
    SigningCredentials GetCurrentSigning();

    /// <summary>
    /// The set of keys a validator must accept. Always contains the current key;
    /// also contains the previous key during a rotation window. Two-element list
    /// is the steady-state ceiling — never more.
    /// </summary>
    IReadOnlyList<SecurityKey> GetValidationKeys();
}

public class RuntimeTokenSigningKeyService : IRuntimeTokenSigningKeyService
{
    public const string Category = "RuntimeTokens";
    public const string CurrentKeyName = "RuntimeTokens:SigningKeyCurrent";
    public const string PreviousKeyName = "RuntimeTokens:SigningKeyPrevious";
    private const string AutoSeedAuthor = "system:auto-seed";
    private const int KeyByteSize = 32;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeTokenSigningKeyService> _logger;

    // Lock around the load-or-seed step. The cached snapshot below is then
    // accessed under the same lock, which keeps the read path correct even
    // when an operator updates the keys mid-flight.
    private readonly object _gate = new();
    private CachedKeys? _cache;

    public RuntimeTokenSigningKeyService(
        IServiceScopeFactory scopeFactory,
        ILogger<RuntimeTokenSigningKeyService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public SigningCredentials GetCurrentSigning()
    {
        var snapshot = LoadOrSeed();
        return new SigningCredentials(snapshot.Current, SecurityAlgorithms.HmacSha256);
    }

    public IReadOnlyList<SecurityKey> GetValidationKeys()
    {
        var snapshot = LoadOrSeed();
        return snapshot.Previous is null
            ? new SecurityKey[] { snapshot.Current }
            : new SecurityKey[] { snapshot.Current, snapshot.Previous };
    }

    /// <summary>
    /// Called by <see cref="EventHandlers.RuntimeTokenSigningKeyCacheInvalidator"/>
    /// (and tests) when an operator updates the keys via the admin UI. Next read
    /// re-pulls from <see cref="ISystemSettingsService"/>.
    /// </summary>
    internal void InvalidateCache()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    private CachedKeys LoadOrSeed()
    {
        // Fast path — already loaded.
        var snapshot = _cache;
        if (snapshot is not null) return snapshot;

        lock (_gate)
        {
            // Double-check under the lock so concurrent first-readers don't both seed.
            if (_cache is not null) return _cache;

            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();

            var currentB64 = settings.Get(CurrentKeyName);
            if (string.IsNullOrWhiteSpace(currentB64))
            {
                var generated = RandomNumberGenerator.GetBytes(KeyByteSize);
                currentB64 = Convert.ToBase64String(generated);
                // SetAsync persists, encrypts, and dispatches SystemSettingChanged. We hold
                // the lock across this await via GetAwaiter().GetResult() — same sync-over-async
                // tradeoff SystemSettingsService.Get() makes for first-read. The auto-seed runs
                // at most once per process restart.
                settings.SetAsync(CurrentKeyName, currentB64, isSecret: true, updatedBy: AutoSeedAuthor)
                    .GetAwaiter().GetResult();
                _logger.LogInformation(
                    "Auto-seeded a new RuntimeToken signing key (Current). " +
                    "Existing tokens — none should exist on first boot — would be invalidated.");
            }

            var current = BuildKey(currentB64!, CurrentKeyName);

            var previousB64 = settings.Get(PreviousKeyName);
            SymmetricSecurityKey? previous = null;
            if (!string.IsNullOrWhiteSpace(previousB64))
            {
                previous = BuildKey(previousB64!, PreviousKeyName);
            }

            _cache = new CachedKeys(current, previous);
            return _cache;
        }
    }

    private static SymmetricSecurityKey BuildKey(string base64, string keyName)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{keyName} is not valid base64. Re-enter via the SystemSettings admin UI.", ex);
        }
        if (bytes.Length != KeyByteSize)
        {
            throw new InvalidOperationException(
                $"{keyName} must decode to exactly {KeyByteSize} bytes (got {bytes.Length}).");
        }
        return new SymmetricSecurityKey(bytes) { KeyId = keyName };
    }

    private sealed record CachedKeys(SymmetricSecurityKey Current, SymmetricSecurityKey? Previous);
}
