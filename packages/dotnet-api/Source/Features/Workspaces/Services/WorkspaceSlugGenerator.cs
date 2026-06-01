using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;

namespace Source.Features.Workspaces.Services;

public sealed class WorkspaceSlugGenerator : IWorkspaceSlugGenerator
{
    private const int MaxSlugLength = 60;
    private const string Fallback = "workspace";

    private readonly ApplicationDbContext _db;

    public WorkspaceSlugGenerator(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<string> GenerateAsync(string seed, CancellationToken cancellationToken = default)
    {
        var baseSlug = Sanitize(seed);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = Fallback;
        }

        // Cap so the suffix can fit. We allow up to 6 chars of suffix ("-9999" + safety).
        const int reservedForSuffix = 6;
        if (baseSlug.Length > MaxSlugLength - reservedForSuffix)
        {
            baseSlug = baseSlug[..(MaxSlugLength - reservedForSuffix)].TrimEnd('-');
        }

        // Pull existing slugs that look like a collision so we don't query in a loop.
        var likePattern = baseSlug + "%";
        var existingSlugs = await _db.Workspaces
            .IgnoreQueryFilters() // include soft-deleted to avoid resurrecting a slug that's "free"
            .Where(w => EF.Functions.Like(w.Slug, likePattern))
            .Select(w => w.Slug)
            .ToListAsync(cancellationToken);

        var taken = new HashSet<string>(existingSlugs, StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        // Astronomically unlikely — but bail rather than spin forever.
        throw new InvalidOperationException($"Could not generate a unique slug for '{seed}' (10000+ collisions)");
    }

    /// <summary>
    /// Pure function: convert any input to a kebab-case ascii lowercase slug.
    /// </summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // Take only the local part if an email was passed in.
        var atSign = input.IndexOf('@');
        if (atSign > 0) input = input[..atSign];

        // Decompose unicode (e.g. ä -> a + diacritic) and strip non-spacing marks.
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        var stripped = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        // Replace runs of non-[a-z0-9] with a single dash.
        var result = new StringBuilder(stripped.Length);
        var prevDash = true; // start true so we don't leak a leading dash
        foreach (var ch in stripped)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (ok)
            {
                result.Append(ch);
                prevDash = false;
            }
            else if (!prevDash)
            {
                result.Append('-');
                prevDash = true;
            }
        }
        var slug = result.ToString().Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug;
    }
}
