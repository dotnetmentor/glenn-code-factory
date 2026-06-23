namespace Source.Features.Conversations.Models;

/// <summary>
/// Canonical string values for the per-conversation / per-session agent backend
/// discriminator. Stored as <c>varchar(32)</c> on <see cref="Conversation"/> and
/// <see cref="AgentSession"/>. Kept as plain strings (not an enum) so the column
/// shape matches the old multi-backend schema's <c>AgentBackend</c> discriminator
/// and so the daemon can speak a new backend value without a coordinated server
/// migration.
/// </summary>
public static class AgentBackends
{
    /// <summary>The Cursor SDK backend — the platform default.</summary>
    public const string Cursor = "cursor";

    /// <summary>The Claude Agent SDK backend.</summary>
    public const string Claude = "claude";

    /// <summary>Max stored length of the discriminator column.</summary>
    public const int MaxLength = 32;

    /// <summary>True when <paramref name="value"/> is a recognised backend.</summary>
    public static bool IsKnown(string? value) =>
        value is Cursor or Claude;
}
