using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Users.Models;

public class User : IdentityUser, IHasDomainEvents, IAuditable, ISoftDelete
{
    private readonly List<IDomainEvent> _domainEvents = new();

    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    // Mobile app onboarding
    public bool IsOnboarded { get; set; } = false;

    // OTP Authentication fields
    public string? OtpCode { get; set; }
    public DateTime? OtpExpiresAt { get; set; }
    public int OtpAttempts { get; set; } = 0;
    public DateTime? LastOtpSentAt { get; set; }

    // Cursor API Integration
    public string? CursorApiKey { get; set; }

    // Credits System
    public int Credits { get; set; } = 500;

    public string FullName => $"{FirstName} {LastName}".Trim();

    // --- Rich domain methods ---

    public void OnCreated()
    {
        RaiseDomainEvent(new UserCreated(Id, Email!, DateTime.UtcNow));
    }

    public Result UpdateProfile(string? firstName, string? lastName)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(firstName) && firstName != FirstName)
        {
            FirstName = firstName.Trim();
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(lastName) && lastName != LastName)
        {
            LastName = lastName.Trim();
            changed = true;
        }

        if (changed)
        {
            RaiseDomainEvent(new UserProfileUpdated(Id, FirstName, LastName));
        }

        return Result.Success();
    }

    // --- OTP helpers ---

    public bool IsOtpValid(string code) =>
        !string.IsNullOrEmpty(OtpCode) &&
        OtpCode == code &&
        OtpExpiresAt > DateTime.UtcNow;

    public bool CanRequestOtp() =>
        LastOtpSentAt == null ||
        DateTime.UtcNow.Subtract(LastOtpSentAt.Value).TotalMinutes >= 1;

    public void ClearOtp()
    {
        OtpCode = null;
        OtpExpiresAt = null;
        OtpAttempts = 0;
        LastOtpSentAt = null;
    }
} 