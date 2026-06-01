using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.SystemSettings.Models;

/// <summary>
/// A single system-level configuration entry. One row per key.
///
/// <para>The primary key is the colon-separated <see cref="Key"/> itself
/// (e.g. <c>GitHub:AppId</c>) — it's a natural unique identifier and lets the
/// service look up rows without a separate id column.</para>
///
/// <para><see cref="Value"/> stores either the cleartext (when <see cref="IsSecret"/>
/// is <c>false</c>) or the encrypted blob — base64 of <c>nonce(12) || ciphertext || tag(16)</c>
/// produced by AES-256-GCM — when secret. <c>null</c> means "no value yet".</para>
/// </summary>
public class SystemSetting : Entity, IAuditable
{
    /// <summary>Colon-separated key, e.g. <c>GitHub:AppId</c>. Primary key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Logical group / UI tab, e.g. <c>GitHub</c>. Indexed for category-scoped reads.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Human-readable description shown in the admin UI. Authored in the catalog.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Drives encryption on write and masking on read.</summary>
    public bool IsSecret { get; set; }

    /// <summary>
    /// Cleartext if <see cref="IsSecret"/> is false; base64(nonce || ciphertext || tag) otherwise.
    /// <c>null</c> when no value has been set yet.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Identity user id of the SuperAdmin who last saved. <c>null</c> for seeded rows.</summary>
    public string? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Apply a new value, encrypting via the supplied cipher when this row is secret.
    /// Raises <see cref="Events.SystemSettingChanged"/> so the cache invalidator picks it up.
    /// </summary>
    public void ApplyValue(string? newValue, string? updatedBy, Services.ISystemSettingsCipher cipher)
    {
        Value = IsSecret && newValue is not null
            ? cipher.Encrypt(newValue)
            : newValue;
        UpdatedBy = updatedBy;
        RaiseDomainEvent(new Events.SystemSettingChanged(Key, Category));
    }
}
