using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Source.Features.ErrorLog.Models;
using Source.Shared.Events;

namespace Source.Infrastructure.Interceptors;

/// <summary>
/// EF Core interceptor that captures property-level changes for ALL entities on every SaveChanges call.
/// This is Layer 2 of the two-layer event architecture (Domain Events via DomainEventInterceptor are Layer 1).
///
/// Captures:
/// - Added entities: all non-null property values (OldValue=null, NewValue=current)
/// - Modified entities: only actually changed properties (OldValue=original, NewValue=current)
/// - Deleted entities: entity type/id with operation "Deleted"
///
/// Skips StoredDomainEvent and StoredEntityChange to avoid tracking the tracking tables.
/// </summary>
public class ChangeTrackingInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    private static readonly HashSet<Type> SkippedTypes = new()
    {
        typeof(StoredDomainEvent),
        typeof(StoredEntityChange),
        typeof(Source.Features.ErrorLog.Models.ErrorLog)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ChangeTrackingInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is null)
            return await base.SavingChangesAsync(eventData, result, ct);

        var userId = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var now = DateTime.UtcNow;

        var entries = eventData.Context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => !SkippedTypes.Contains(e.Entity.GetType()))
            .ToList();

        foreach (var entry in entries)
        {
            var entityType = entry.Entity.GetType().Name;
            var entityId = GetPrimaryKeyValue(entry);
            var operation = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Modified => "Updated",
                EntityState.Deleted => "Deleted",
                _ => "Unknown"
            };

            var changedProperties = new List<object>();

            if (entry.State == EntityState.Added)
            {
                foreach (var property in entry.Properties)
                {
                    // Skip navigation properties (only track scalar properties)
                    if (property.Metadata.IsShadowProperty())
                        continue;

                    if (property.CurrentValue is not null)
                    {
                        changedProperties.Add(new
                        {
                            Property = property.Metadata.Name,
                            OldValue = (string?)null,
                            NewValue = ConvertToString(property.CurrentValue)
                        });
                    }
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                foreach (var property in entry.Properties)
                {
                    if (property.Metadata.IsShadowProperty())
                        continue;

                    if (property.IsModified)
                    {
                        changedProperties.Add(new
                        {
                            Property = property.Metadata.Name,
                            OldValue = ConvertToString(property.OriginalValue),
                            NewValue = ConvertToString(property.CurrentValue)
                        });
                    }
                }
            }
            // For Deleted, we just record the operation with no changed properties

            var storedChange = new StoredEntityChange
            {
                Id = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                ChangedProperties = JsonSerializer.Serialize(changedProperties),
                UserId = userId,
                OccurredAt = now
            };

            eventData.Context.Set<StoredEntityChange>().Add(storedChange);
        }

        return await base.SavingChangesAsync(eventData, result, ct);
    }

    private static string GetPrimaryKeyValue(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var keyProperties = entry.Metadata.FindPrimaryKey()?.Properties;
        if (keyProperties is null || keyProperties.Count == 0)
            return string.Empty;

        if (keyProperties.Count == 1)
        {
            var value = entry.Property(keyProperties[0].Name).CurrentValue;
            return value?.ToString() ?? string.Empty;
        }

        // Composite key: join values with separator
        var values = keyProperties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty);
        return string.Join("_", values);
    }

    private static string? ConvertToString(object? value)
    {
        if (value is null) return null;
        if (value is DateTime dt) return dt.ToString("O");
        if (value is DateTimeOffset dto) return dto.ToString("O");

        // Simple scalars stay as plain strings
        if (value is string s) return s;
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid)
            return value.ToString();
        if (value.GetType().IsEnum) return value.ToString();

        // JsonElement — return raw JSON text
        if (value is JsonElement je)
            return je.GetRawText();

        // Complex types (collections, dictionaries, objects) — serialize as JSON
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch
        {
            return value.ToString();
        }
    }
}
