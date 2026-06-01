using Microsoft.EntityFrameworkCore;
using Source.Features.SystemSettings.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.SystemSettings.Queries;

/// <summary>
/// Lists every catalog-registered setting joined with its current DB row (if any).
/// Returns shape suitable for the admin UI: catalog metadata (label, description, isSecret)
/// merged with DB state (hasValue, updatedAt) and — for non-secret rows — the cleartext value.
///
/// <para><b>Secret-handling contract:</b> the response NEVER includes cleartext for rows
/// flagged secret in the catalog. <see cref="ISystemSettingsService.Get"/> is only called
/// for non-secret keys; secret rows surface as <c>{ HasValue: true/false, Value: null }</c>.
/// </para>
/// </summary>
public record GetSystemSettingsQuery : IQuery<Result<IReadOnlyList<SystemSettingDto>>>;

public record SystemSettingDto
{
    public required string Key { get; init; }
    public required string Category { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool IsSecret { get; init; }
    public required bool HasValue { get; init; }
    /// <summary>Cleartext value for non-secret rows; always <c>null</c> for secrets.</summary>
    public string? Value { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public class GetSystemSettingsHandler
    : IQueryHandler<GetSystemSettingsQuery, Result<IReadOnlyList<SystemSettingDto>>>
{
    private readonly ApplicationDbContext _db;
    private readonly ISystemSettingsService _settings;

    public GetSystemSettingsHandler(ApplicationDbContext db, ISystemSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<Result<IReadOnlyList<SystemSettingDto>>> Handle(
        GetSystemSettingsQuery request, CancellationToken cancellationToken)
    {
        // Pull every row up-front; the catalog drives ordering + display metadata,
        // the DB provides current state. For "huge" registries this would page,
        // but we're talking dozens of rows max.
        var rows = await _db.Set<SystemSetting>()
            .AsNoTracking()
            .ToDictionaryAsync(r => r.Key, cancellationToken);

        var ordered = new List<SystemSettingDto>();

        foreach (var category in SystemSettingsCatalog.Categories)
        {
            foreach (var def in category.Settings)
            {
                rows.TryGetValue(def.Key, out var dbRow);

                // Empty-string seeded values count as "no value" for the UI — operators see
                // an empty input box with the placeholder, not a literal empty string.
                var hasValue = !string.IsNullOrEmpty(dbRow?.Value);

                // Catalog wins over DB for IsSecret — the catalog defines policy.
                // For secrets we deliberately skip Get() to avoid a needless decrypt round
                // trip (and to make sure no cleartext ever touches the response object).
                string? value = null;
                if (!def.IsSecret && hasValue)
                {
                    value = _settings.Get(def.Key);
                }

                ordered.Add(new SystemSettingDto
                {
                    Key = def.Key,
                    Category = def.Category,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    IsSecret = def.IsSecret,
                    HasValue = hasValue,
                    Value = value,
                    UpdatedAt = dbRow?.UpdatedAt,
                });
            }
        }

        return Result.Success<IReadOnlyList<SystemSettingDto>>(ordered);
    }
}
