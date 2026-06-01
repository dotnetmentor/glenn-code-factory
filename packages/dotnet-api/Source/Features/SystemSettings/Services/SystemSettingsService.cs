using Microsoft.EntityFrameworkCore;
using Source.Features.SystemSettings.Models;
using Source.Infrastructure;

namespace Source.Features.SystemSettings.Services;

/// <summary>
/// Read/write façade over the SystemSettings store. Reads are synchronous and
/// served from the in-process <see cref="SystemSettingsCache"/>; the first read of
/// a category triggers a single DB roundtrip that populates the cache for that
/// whole category at once.
///
/// <para><b>Sync vs async trade-off.</b> <see cref="Get"/> and <see cref="GetSection{T}"/>
/// are synchronous to mirror the <see cref="Microsoft.Extensions.Options.IOptions{T}"/>
/// consumption pattern (so call sites swap with minimal churn). When the cache is cold,
/// the service blocks the calling thread on a DB call via <c>GetAwaiter().GetResult()</c>.
/// The blocking happens at most once per category per process restart, so it's a
/// pragmatic choice — operators worried about it can warm the cache at startup with
/// <see cref="PreloadAsync"/>.</para>
/// </summary>
public interface ISystemSettingsService
{
    /// <summary>Returns the decrypted value, or <c>null</c> if unset/unknown. Lazy-loads the category.</summary>
    string? Get(string key);

    /// <summary>Populates <typeparamref name="T"/> by reading every <c>{prefix}:{Property}</c> key from the cache.</summary>
    T GetSection<T>(string prefix) where T : new();

    /// <summary>
    /// Upsert a single setting. Encrypts when <paramref name="isSecret"/> is true.
    /// Raises a <see cref="Events.SystemSettingChanged"/> domain event on the entity so
    /// the cache invalidator drops the cached category.
    /// </summary>
    Task SetAsync(string key, string? value, bool isSecret, string? updatedBy = null, CancellationToken ct = default);

    /// <summary>Drops the cached entry for a category. Next read re-fetches from the DB.</summary>
    void InvalidateCategory(string category);

    /// <summary>Eagerly load a category into the cache. Optional warm-up for latency-sensitive paths.</summary>
    Task PreloadAsync(string category, CancellationToken ct = default);
}

public class SystemSettingsService : ISystemSettingsService
{
    private readonly ApplicationDbContext _db;
    private readonly SystemSettingsCache _cache;
    private readonly ISystemSettingsCipher _cipher;

    public SystemSettingsService(
        ApplicationDbContext db,
        SystemSettingsCache cache,
        ISystemSettingsCipher cipher)
    {
        _db = db;
        _cache = cache;
        _cipher = cipher;
    }

    public string? Get(string key)
    {
        var category = ExtractCategory(key);
        var values = LoadCategory(category);
        return values.TryGetValue(key, out var value) ? value : null;
    }

    public T GetSection<T>(string prefix) where T : new()
    {
        var category = ExtractCategory(prefix);
        var values = LoadCategory(category);
        var instance = new T();
        var props = typeof(T).GetProperties();
        foreach (var prop in props)
        {
            if (!prop.CanWrite) continue;
            var compositeKey = $"{prefix}:{prop.Name}";
            if (values.TryGetValue(compositeKey, out var raw) && raw is not null)
            {
                // Only string properties are populated. SystemSetting.Value is text — there's no
                // meaningful coercion path to int/bool/etc that we'd want to support implicitly.
                if (prop.PropertyType == typeof(string))
                {
                    prop.SetValue(instance, raw);
                }
            }
        }
        return instance;
    }

    public async Task SetAsync(
        string key,
        string? value,
        bool isSecret,
        string? updatedBy = null,
        CancellationToken ct = default)
    {
        var category = ExtractCategory(key);
        var existing = await _db.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == key, ct);

        if (existing is null)
        {
            existing = new SystemSetting
            {
                Key = key,
                Category = category,
                IsSecret = isSecret,
                Description = string.Empty,
            };
            existing.ApplyValue(value, updatedBy, _cipher);
            _db.Set<SystemSetting>().Add(existing);
        }
        else
        {
            // IsSecret is authoritative from the catalog — refresh in case it changed.
            existing.IsSecret = isSecret;
            existing.ApplyValue(value, updatedBy, _cipher);
        }

        await _db.SaveChangesAsync(ct);
        // Cache invalidation also runs via the SystemSettingChanged domain event handler
        // (push-based). Doing it here too is belt-and-braces — the invalidate is idempotent.
        InvalidateCategory(category);
    }

    public void InvalidateCategory(string category) => _cache.Invalidate(category);

    public async Task PreloadAsync(string category, CancellationToken ct = default)
    {
        var rows = await _db.Set<SystemSetting>()
            .AsNoTracking()
            .Where(s => s.Category == category)
            .ToListAsync(ct);
        _cache.SetCategory(category, BuildLookup(rows));
    }

    private IReadOnlyDictionary<string, string?> LoadCategory(string category)
    {
        if (_cache.TryGetCategory(category, out var cached))
            return cached;

        // Sync-over-async: one-shot per category per process restart. See class doc.
        var rows = _db.Set<SystemSetting>()
            .AsNoTracking()
            .Where(s => s.Category == category)
            .ToList();

        var built = BuildLookup(rows);
        _cache.SetCategory(category, built);
        return built;
    }

    private IReadOnlyDictionary<string, string?> BuildLookup(IReadOnlyList<SystemSetting> rows)
    {
        var dict = new Dictionary<string, string?>(rows.Count);
        foreach (var row in rows)
        {
            dict[row.Key] = DecryptIfNeeded(row);
        }
        return dict;
    }

    private string? DecryptIfNeeded(SystemSetting row)
    {
        if (row.Value is null) return null;
        if (!row.IsSecret) return row.Value;
        try
        {
            return _cipher.Decrypt(row.Value);
        }
        catch
        {
            // A row that won't decrypt (key rotation, manual tampering) is treated as "unset"
            // for read purposes rather than crashing the calling code. The admin UI surfaces
            // it as "not set" so the operator can re-enter it.
            return null;
        }
    }

    private static string ExtractCategory(string keyOrPrefix)
    {
        var idx = keyOrPrefix.IndexOf(':');
        return idx < 0 ? keyOrPrefix : keyOrPrefix[..idx];
    }
}
