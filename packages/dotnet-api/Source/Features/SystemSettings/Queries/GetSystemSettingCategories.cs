using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.SystemSettings.Queries;

/// <summary>
/// Pure projection of <see cref="SystemSettingsCatalog.Categories"/> for the admin UI.
/// Drives the tab order and the per-field labels/descriptions. No DB call — schema is in code.
/// </summary>
public record GetSystemSettingCategoriesQuery : IQuery<Result<IReadOnlyList<SystemSettingCategoryDto>>>;

public record SystemSettingCategoryDto
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<SystemSettingDefinitionDto> Settings { get; init; }
}

public record SystemSettingDefinitionDto
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required bool IsSecret { get; init; }
}

public class GetSystemSettingCategoriesHandler
    : IQueryHandler<GetSystemSettingCategoriesQuery, Result<IReadOnlyList<SystemSettingCategoryDto>>>
{
    public Task<Result<IReadOnlyList<SystemSettingCategoryDto>>> Handle(
        GetSystemSettingCategoriesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<SystemSettingCategoryDto> categories = SystemSettingsCatalog.Categories
            .Select(c => new SystemSettingCategoryDto
            {
                Key = c.Key,
                DisplayName = c.DisplayName,
                Description = c.Description,
                Settings = c.Settings.Select(s => new SystemSettingDefinitionDto
                {
                    Key = s.Key,
                    DisplayName = s.DisplayName,
                    Description = s.Description,
                    IsSecret = s.IsSecret,
                }).ToList(),
            })
            .ToList();

        return Task.FromResult(Result.Success(categories));
    }
}
