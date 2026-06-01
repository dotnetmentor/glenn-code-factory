using Source.Features.Conversations.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Attachments.Models;

/// <summary>
/// One file the user attached to a chat conversation. The row is created when
/// the browser asks the backend for a presigned PUT URL
/// (<c>POST /api/attachments/presign</c>) and progresses through:
///
/// <list type="number">
///   <item>Created: row exists, <see cref="UploadedAt"/> = null. The browser is
///         pushing bytes directly to R2 via the presigned URL — backend never
///         sees them.</item>
///   <item>Uploaded: browser calls <c>POST /api/attachments/{id}/complete</c>
///         and <see cref="UploadedAt"/> is stamped. The file is durable in R2.</item>
///   <item>Staged: a follow-up card wires the daemon to copy the file to the
///         runtime's local FS and PATCH back; <see cref="StagedAt"/> is stamped
///         then. Until then it is null. (Not used by this card — placeholder
///         column so the migration is one-shot.)</item>
/// </list>
///
/// <para><b>Persistence shape.</b> Soft-deletable + audited via the project's
/// <see cref="ISoftDelete"/> / <see cref="IAuditable"/> interceptors. The
/// soft-delete query filter hides removed attachments from default queries.
/// Inherits <see cref="Entity"/> so future state transitions can raise domain
/// events (none in v1).</para>
///
/// <para><b>Storage key shape.</b> <see cref="R2Key"/> is
/// <c>attachments/{conversationId}/{attachmentId}-{sanitizedFileName}</c>. The
/// conversation-id prefix lets us scope cleanup / listing if we ever need it;
/// the attachment-id segment guarantees uniqueness even when the user attaches
/// two files with the same name in one composer (see the spec's Edge Cases).</para>
/// </summary>
public class Attachment : Entity, IAuditable, ISoftDelete
{
    public const int MaxFileNameLength = 512;
    public const int MaxContentTypeLength = 256;
    public const int MaxR2KeyLength = 1024;

    /// <summary>50 MiB. Enforced server-side at the presign endpoint AND
    /// (separately) client-side before any upload starts.</summary>
    public const long MaxSizeBytes = 50L * 1024L * 1024L;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Conversation this attachment belongs to. FK indexed.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Navigation to the parent conversation.</summary>
    public Conversation Conversation { get; set; } = null!;

    /// <summary>Original filename as supplied by the browser (e.g.
    /// <c>vendor-proposal.pdf</c>). Trimmed; not sanitised here — the storage
    /// key carries a sanitised variant.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Best-effort MIME type the browser reported. Null when the
    /// browser couldn't determine one (drag-and-drop of an unknown
    /// extension).</summary>
    public string? ContentType { get; set; }

    /// <summary>File size in bytes as reported by the browser. Validated
    /// <c>&gt; 0</c> AND <c>≤ <see cref="MaxSizeBytes"/></c> at presign time.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Object key inside the R2 bucket. Layout:
    /// <c>attachments/{conversationId}/{attachmentId}-{sanitizedFileName}</c>.</summary>
    public string R2Key { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the browser confirmed the direct-to-R2 PUT
    /// succeeded via <c>POST /api/attachments/{id}/complete</c>. Null until
    /// that handshake lands; chip stays in "Uploading" / "Staging" UX states
    /// while this is null.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>UTC timestamp when the daemon confirmed the file is staged at
    /// the runtime's local FS path. Null until the follow-up "daemon staging"
    /// card lands; this card just persists the column.</summary>
    public DateTime? StagedAt { get; set; }

    /// <summary>
    /// FK to the <see cref="AgentSession"/> ("turn / user message") that sent
    /// this attachment. <c>null</c> while the attachment is sitting in a draft
    /// composer (post-presign, pre-send); stamped to the freshly-created
    /// session id at <c>SubmitPrompt</c> time so past-message chips can
    /// re-render the attachments that belonged to a given turn.
    ///
    /// <para>Chat is event-sourced — there is no separate <c>Message</c> table.
    /// The "user message" of a turn is the <c>PromptReceived</c>
    /// <see cref="AgentEvent"/> at <c>Sequence = 0</c> of a session. Linking to
    /// the session id (rather than to a composite event PK or to a synthetic
    /// message id) keeps the FK trivial and matches the 1:1 nature of "one
    /// user message per session".</para>
    ///
    /// <para>SetNull on delete: sessions can be terminal / archived but we
    /// never hard-delete a session in v1; if that ever changes we want the
    /// attachment row to stay reachable through <see cref="ConversationId"/>.</para>
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>Navigation to the parent session (the turn the attachment was
    /// sent in). <c>null</c> until <see cref="SessionId"/> is stamped at
    /// send-time. Lets queries pull "attachments for session X" without a
    /// separate join.</summary>
    public AgentSession? Session { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
