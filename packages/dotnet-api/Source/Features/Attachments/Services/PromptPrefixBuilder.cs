using System.Text;
using Source.Features.Attachments.Models;

namespace Source.Features.Attachments.Services;

/// <summary>
/// Builds the per-turn prompt prefix the agent sees when the user attached one
/// or more files. The format is dictated by the <c>chat-file-attachments</c>
/// spec under "Architectural Guidelines → Prompt injection format" and is
/// deliberately identical for one or many attachments — the agent's behaviour
/// must not depend on how many files came with the turn.
///
/// <para><b>Why static / no DI.</b> Pure string formatting; no DB, no clock,
/// no logger. Trivially unit-testable. Lives in <c>Attachments/Services</c>
/// because it's the only consumer of <see cref="Attachment"/> outside the
/// feature's own commands/queries, but exposes no behaviour beyond rendering
/// the block.</para>
///
/// <para><b>Why the path comes from <see cref="LocalPathFor"/> and not the
/// entity.</b> The on-runtime local path is a derived value
/// (<c>/data/project/repo/.glenn/attachments/{conversationId}/{attachmentId}-{filename}</c>) — see
/// <c>chat-file-attachments</c> Card 1's spike. Keeping the derivation here
/// means there is exactly one source of truth for the path the prompt prefix
/// references AND the path the daemon stages to (both compute it from the
/// same attachment id + filename). The cloud R2 key is intentionally NOT used
/// — the model only ever sees local paths per the spec.</para>
/// </summary>
public static class PromptPrefixBuilder
{
    /// <summary>
    /// Return <paramref name="userPrompt"/> unchanged when there are no
    /// attachments; otherwise prepend the per-file-line block + blank line.
    /// Format reproduced verbatim from the spec's "Prompt injection format"
    /// section:
    /// <code>
    /// [Attached files for this turn:
    ///   - report.pdf  -&gt;  /data/project/repo/.glenn/attachments/{conv}/{id}-report.pdf
    ///   - logo.png    -&gt;  /data/project/repo/.glenn/attachments/{conv}/{id}-logo.png
    /// You can read them with the Read tool.]
    ///
    /// {user's message}
    /// </code>
    /// One bullet per attachment, no alignment / padding (let the agent
    /// tokenise the simplest possible structure). ASCII <c>-&gt;</c> separator
    /// rather than the Unicode arrow shown in the spec so the prefix renders
    /// identically in any logging surface that might not be UTF-8 — the agent
    /// reads either fine.
    /// </summary>
    public static string BuildPromptWithAttachments(string userPrompt, IReadOnlyList<Attachment> attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            // Zero-attachments path stays a byte-for-byte passthrough so the
            // daemon-side prompt rendering and any analytics dedup on the
            // prompt text don't see a phantom prefix when nothing was attached.
            return userPrompt;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Attached files for this turn:");
        foreach (var a in attachments)
        {
            sb.AppendLine($"  - {a.FileName}  ->  {LocalPathFor(a)}");
        }
        sb.AppendLine("You can read them with the Read tool.]");
        sb.AppendLine();
        sb.Append(userPrompt);
        return sb.ToString();
    }

    /// <summary>
    /// The on-runtime local path the daemon stages an attachment to. Mirrored
    /// in <c>chat-file-attachments</c> Card 1 (spike) and reused by the
    /// staging handshake in Card 4. Keeping the helper public lets tests
    /// assert against the same derivation the production code uses.
    ///
    /// <para>Staged INSIDE the repo at a gitignored location
    /// (<c>/data/project/repo/.glenn/attachments/...</c>) so the Cursor agent
    /// — whose cwd is the repo root (<c>/data/project/repo</c>) — can read the
    /// file with a relative or absolute Read. The <c>.glenn/</c> directory is
    /// gitignored so staged attachments never get committed.</para>
    /// </summary>
    public static string LocalPathFor(Attachment a) =>
        $"/data/project/repo/.glenn/attachments/{a.ConversationId}/{a.Id}-{a.FileName}";
}
