namespace Source.Features.RuntimePresets.Services;

/// <summary>
/// Lookup the list of installable mise tool versions for a given tool name.
/// Backs the "Lookup versions" button next to <see cref="Models.PresetParameter.MiseTool"/>
/// inputs in the super-admin preset editor — saves the operator from having to
/// memorise the current list of supported runtimes.
///
/// <para><b>v1 — hardcoded cache.</b> The interface exists from day 1 so the
/// admin UI flow is wired end-to-end; the implementation just returns a
/// hand-curated dictionary. A future revision can swap in a real
/// <c>mise ls-remote</c> shell-out or a periodic refresh from the mise GitHub
/// registry without touching callers.</para>
/// </summary>
public interface IMiseVersionLookup
{
    /// <summary>
    /// Return the list of installable versions for <paramref name="tool"/>
    /// (case-insensitive — operators may type <c>"Dotnet"</c>). Returns an
    /// empty list (never null) when the tool is unknown — the UI surfaces this
    /// as "no versions found" without throwing.
    /// </summary>
    Task<List<string>> GetVersionsAsync(string tool, CancellationToken ct = default);
}

/// <summary>
/// Hardcoded-cache implementation of <see cref="IMiseVersionLookup"/>.
/// Registered as a singleton in <c>Program.cs</c> — the static
/// <see cref="Versions"/> dictionary is the entire state, no per-request work
/// happens here. Refreshing the list means editing this file and redeploying.
///
/// <para><b>Versions chosen.</b> The most recent point release of each
/// supported major as of the v3 cutover, plus the immediately-previous major
/// so operators can pin a legacy runtime when a customer needs it. The exact
/// set isn't load-bearing; the admin UI lets the operator type a free-form
/// version too — the dropdown is a convenience, not a constraint.</para>
/// </summary>
public sealed class MiseVersionLookup : IMiseVersionLookup
{
    /// <summary>
    /// Tool → ordered version list. Stable so the admin UI never shows a
    /// flickering order across requests; oldest-first matches <c>mise ls-remote</c>
    /// output convention (operators tend to scroll to the bottom for "latest").
    /// </summary>
    private static readonly Dictionary<string, List<string>> Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = new() { "7.0.20", "8.0.11", "9.0.0", "9.0.1" },
        ["node"] = new() { "18.20.5", "20.18.0", "22.11.0" },
        ["python"] = new() { "3.10.15", "3.11.10", "3.12.7", "3.13.0" },
        ["go"] = new() { "1.21.13", "1.22.9", "1.23.3" },
        ["ruby"] = new() { "3.2.5", "3.3.6" },
        ["postgres"] = new() { "14.13", "15.8", "16.4" },
    };

    public Task<List<string>> GetVersionsAsync(string tool, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return Task.FromResult(new List<string>());
        }

        if (Versions.TryGetValue(tool, out var list))
        {
            // Return a defensive copy so callers can't mutate the static cache.
            return Task.FromResult(new List<string>(list));
        }
        return Task.FromResult(new List<string>());
    }
}
