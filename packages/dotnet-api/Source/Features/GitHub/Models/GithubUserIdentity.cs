using Source.Features.Users.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.GitHub.Models;

/// <summary>
/// Links an application <see cref="User"/> to their GitHub account identity.
/// Created on first GitHub-OAuth login (or attached to an existing logged-in user).
/// </summary>
public class GithubUserIdentity : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>FK to <see cref="User.Id"/> (AspNetUsers).</summary>
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }

    /// <summary>GitHub-side numeric user id. Stable across login changes.</summary>
    public long GithubUserId { get; set; }

    /// <summary>Current GitHub login at the time of the link (informational; can change).</summary>
    public string GithubLogin { get; set; } = string.Empty;

    /// <summary>Avatar URL from GitHub (informational).</summary>
    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
