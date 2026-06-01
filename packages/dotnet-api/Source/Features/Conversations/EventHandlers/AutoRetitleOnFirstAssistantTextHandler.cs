using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// First-AssistantText auto-retitle handler. Listens to every
/// <see cref="AgentEventEmitted"/> the daemon raises and, on the first
/// <see cref="AgentEventType.AssistantText"/> chunk for a conversation that
/// still has <see cref="Conversation.IsAutoTitled"/> == <c>true</c>, derives a
/// short heuristic title from the assistant's response and flips the title
/// once. Subsequent AssistantText events are short-circuited by the
/// <see cref="Conversation.AutoRetitle"/> guard.
///
/// <para><b>Heuristic.</b> Trim → split on <c>.</c> / <c>!</c> / <c>?</c> and
/// take the part before the first one. If that yields nothing usable (no
/// terminator, or the first sentence is &gt; 60 chars) fall back to the first
/// ~8 whitespace-separated words. Capitalise the first character and drop
/// trailing whitespace / punctuation. The rich-entity
/// <see cref="Conversation.AutoRetitle"/> method then caps at 60 chars with a
/// word-boundary truncation + ellipsis.</para>
///
/// <para><b>Reliability contract.</b> Auto-titling is best-effort UX polish;
/// failure here must never poison the broader event-dispatch chain or block
/// the user's turn. The entire handler body is wrapped in a try/catch that
/// logs and swallows — mirroring the defensive shape used by
/// <c>BroadcastAgentEventHandler</c>.</para>
/// </summary>
public class AutoRetitleOnFirstAssistantTextHandler : IEventHandler<AgentEventEmitted>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AutoRetitleOnFirstAssistantTextHandler> _logger;

    // Hard cap on the derived title (used by the local heuristic before
    // handing off to Conversation.AutoRetitle, which enforces the same cap).
    private const int TitleCap = 60;

    // Fallback word count when the first-sentence heuristic doesn't land.
    private const int FallbackWordCount = 8;

    public AutoRetitleOnFirstAssistantTextHandler(
        ApplicationDbContext db,
        ILogger<AutoRetitleOnFirstAssistantTextHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Handle(AgentEventEmitted notification, CancellationToken cancellationToken)
    {
        try
        {
            // Filter: only the AssistantText path triggers retitle. Every other
            // kind (PromptReceived, ToolUse, Thinking, ...) is a no-op.
            if (notification.Kind != AgentEventKind.AssistantText)
            {
                return;
            }

            // Cheap pre-check: skip the DB roundtrip if the conversation has
            // already been renamed (user-edit or a previous AssistantText). We
            // still re-check inside AutoRetitle to guard against the race where
            // two AssistantText events fire in parallel.
            var conversation = await _db.Conversations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == notification.ConversationId, cancellationToken);

            if (conversation is null)
            {
                _logger.LogDebug(
                    "AutoRetitleOnFirstAssistantTextHandler: conversation {ConversationId} not found; skipping.",
                    notification.ConversationId);
                return;
            }

            if (!conversation.IsAutoTitled)
            {
                // Already renamed (user or previous auto-retitle). Idempotent skip.
                return;
            }

            // Cursor-native shape: AssistantText carries the body in the
            // first-class Text column. The AgentEventEmitted payload no longer
            // includes that body inline (card 2 stripped EventData), so we
            // re-read it from the row by PK. Cheap composite-PK lookup.
            var rawText = await _db.AgentEvents
                .AsNoTracking()
                .Where(e => e.SessionId == notification.SessionId
                         && e.Sequence == notification.Sequence)
                .Select(e => e.Text)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _logger.LogDebug(
                    "AutoRetitleOnFirstAssistantTextHandler: empty / non-text payload for conversation {ConversationId} (seq {Sequence}); skipping.",
                    notification.ConversationId, notification.Sequence);
                return;
            }

            var derived = DeriveTitle(rawText);
            if (string.IsNullOrWhiteSpace(derived))
            {
                // Whitespace-only / punctuation-only chunk — wait for the next
                // AssistantText event to try again. Don't flip IsAutoTitled.
                _logger.LogDebug(
                    "AutoRetitleOnFirstAssistantTextHandler: heuristic produced empty title for conversation {ConversationId} (seq {Sequence}); skipping.",
                    notification.ConversationId, notification.Sequence);
                return;
            }

            var result = conversation.AutoRetitle(derived);
            if (result.IsFailure)
            {
                _logger.LogDebug(
                    "AutoRetitleOnFirstAssistantTextHandler: AutoRetitle refused for conversation {ConversationId} ({Reason}); skipping.",
                    notification.ConversationId, result.Error);
                return;
            }

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "AutoRetitleOnFirstAssistantTextHandler: retitled conversation {ConversationId} to \"{Title}\" off seq {Sequence}.",
                notification.ConversationId, conversation.Title, notification.Sequence);
        }
        catch (Exception ex)
        {
            // Auto-titling failure must never poison the event chain. The
            // user's turn proceeds; the conversation just keeps its placeholder
            // title. A subsequent user rename or a follow-up AssistantText
            // event will take another shot at it.
            _logger.LogWarning(ex,
                "AutoRetitleOnFirstAssistantTextHandler: failed to derive / apply auto-title for conversation {ConversationId} (seq {Sequence}); persistence is unaffected.",
                notification.ConversationId, notification.Sequence);
        }
    }

    // ----------------------------------------------------------------------
    // Pure helpers (kept private + static so they're easy to read top-down)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Heuristic title derivation. See the class-level doc for the recipe.
    /// Pure function; returns <c>string.Empty</c> when no usable title can be
    /// pulled from the input (which the caller treats as "skip and wait for
    /// the next AssistantText event").
    /// </summary>
    private static string DeriveTitle(string rawText)
    {
        // Collapse leading / trailing whitespace + newlines.
        var trimmed = rawText.Trim();
        if (trimmed.Length == 0) return string.Empty;

        // Find the first sentence terminator. Take the slice BEFORE it.
        var firstTerminator = trimmed.IndexOfAny(new[] { '.', '!', '?' });
        string candidate;
        if (firstTerminator > 0 && firstTerminator <= TitleCap)
        {
            // Use the first sentence, BEFORE the terminator.
            candidate = trimmed.Substring(0, firstTerminator);
        }
        else
        {
            // No terminator (or the first sentence is too long) — fall back to
            // the first ~8 whitespace-separated words.
            var words = trimmed.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return string.Empty;
            candidate = string.Join(
                ' ',
                words.Take(Math.Min(FallbackWordCount, words.Length)));
        }

        // Strip trailing whitespace + dangling punctuation that survived the
        // sentence split (e.g. a trailing comma from "Hello, world").
        candidate = candidate.TrimEnd().TrimEnd(',', ';', ':', '-', '—', '–');

        if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;

        // Cap at 60 chars locally too — AutoRetitle will also enforce, but
        // doing it here keeps the candidate clean for the word-boundary
        // truncation downstream.
        if (candidate.Length > TitleCap)
        {
            var hardCut = candidate.Substring(0, TitleCap);
            var lastSpace = hardCut.LastIndexOf(' ');
            if (lastSpace >= TitleCap - 10 && lastSpace > 0)
            {
                hardCut = hardCut.Substring(0, lastSpace);
            }
            candidate = hardCut.TrimEnd();
        }

        // Capitalise just the first character — do NOT title-case the whole
        // thing (preserves things like "useEffect" or acronyms mid-title).
        if (candidate.Length > 0 && char.IsLetter(candidate[0]) && !char.IsUpper(candidate[0]))
        {
            candidate = char.ToUpperInvariant(candidate[0]) + candidate.Substring(1);
        }

        return candidate;
    }
}
