using System.Collections.Concurrent;

namespace Source.Features.SystemSettings.Services;

/// <summary>
/// Singleton in-memory cache of decrypted SystemSetting values, keyed by category.
/// Holds plaintext for both secret and non-secret rows — same trade-off
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> makes today.
///
/// <para>Cache shape: outer key = category (e.g. <c>GitHub</c>);
/// inner map = full setting key (e.g. <c>GitHub:AppId</c>) to its current value.
/// Per-category granularity means one DB roundtrip loads everything under a tab,
/// which is what <see cref="ISystemSettingsService.GetSection{T}"/> needs.</para>
///
/// <para>The cache outlives any DI scope. Mutations go through
/// <see cref="ISystemSettingsService"/>, which both writes the row and invalidates
/// the relevant entry here so subsequent reads pick up the new value.</para>
/// </summary>
public class SystemSettingsCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string?>> _byCategory = new();

    public bool TryGetCategory(string category, out IReadOnlyDictionary<string, string?> values)
        => _byCategory.TryGetValue(category, out values!);

    public void SetCategory(string category, IReadOnlyDictionary<string, string?> values)
        => _byCategory[category] = values;

    public void Invalidate(string category)
        => _byCategory.TryRemove(category, out _);

    public void Clear() => _byCategory.Clear();
}
