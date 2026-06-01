namespace Source.Features.ProjectKanban.Commands;

/// <summary>
/// Validation primitives shared across the Kanban command handlers. Mirrors the
/// <c>SecretValidation</c> pattern in <c>Features.ProjectSecrets.Commands</c> —
/// pure functions returning <c>null</c> on success or an <c>error_code</c>
/// string on failure so the handler can wrap with <c>Result.Failure(...)</c>.
///
/// <para><b>Title cap.</b> Spec 15 Card 3 caps title length at 200 chars (the
/// entity column is 500 to leave room for product changes; the command-level
/// cap is the contract clients see).</para>
/// </summary>
internal static class KanbanCardValidation
{
    public const int MaxTitleLength = 200;

    /// <summary>
    /// Validate a kanban card title. Required, trimmed, max <see cref="MaxTitleLength"/>.
    /// Returns the error code on failure, <c>null</c> on success.
    /// </summary>
    public static string? ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "invalid_title";
        }
        if (title.Length > MaxTitleLength)
        {
            return "invalid_title";
        }
        return null;
    }
}
