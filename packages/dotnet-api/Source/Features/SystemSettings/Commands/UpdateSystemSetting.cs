using Microsoft.EntityFrameworkCore;
using Source.Features.SystemSettings.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.SystemSettings.Commands;

/// <summary>
/// Upsert a single system setting by key. <c>IsSecret</c> is authoritative from
/// <see cref="SystemSettingsCatalog"/> — the request body cannot influence it.
///
/// <para><b>Empty-string-vs-null contract for secrets:</b>
/// <list type="bullet">
///   <item><c>Value = null</c> — clear the row's value.</item>
///   <item><c>Value = ""</c> AND row is secret AND it currently has a value — no-op
///   (the UI sends "" for an unchanged secret box; treat that as "keep what's there").</item>
///   <item>Otherwise — write the new value (encrypting if secret).</item>
/// </list>
/// </para>
/// </summary>
public record UpdateSystemSettingCommand(string Key, string? Value, string? UpdatedByUserId)
    : ICommand<Result>;

public class UpdateSystemSettingHandler : ICommandHandler<UpdateSystemSettingCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly ISystemSettingsService _settings;

    public UpdateSystemSettingHandler(ApplicationDbContext db, ISystemSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<Result> Handle(UpdateSystemSettingCommand request, CancellationToken cancellationToken)
    {
        var def = SystemSettingsCatalog.FindByKey(request.Key);
        if (def is null)
        {
            return Result.Failure("Unknown setting key");
        }

        // Empty-string-keep-existing only applies to secret rows that already have a value.
        if (def.IsSecret && request.Value == string.Empty)
        {
            var existing = await _db.Set<SystemSetting>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == request.Key, cancellationToken);

            if (existing is { Value: { Length: > 0 } })
            {
                // Don't touch anything — UI sent the empty placeholder, not an actual change.
                return Result.Success();
            }
        }

        await _settings.SetAsync(
            request.Key,
            request.Value,
            def.IsSecret,
            updatedBy: request.UpdatedByUserId,
            ct: cancellationToken);

        return Result.Success();
    }
}
