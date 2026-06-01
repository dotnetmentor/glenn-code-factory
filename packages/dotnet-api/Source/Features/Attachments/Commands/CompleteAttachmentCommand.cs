using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Models;
using Source.Features.Attachments.Services;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Attachments.Commands;

/// <summary>
/// Mark an attachment as uploaded — the browser calls this after the
/// direct-to-R2 PUT succeeds. Idempotent: a second call on an already-complete
/// attachment returns the same response shape (with the existing
/// <see cref="Attachment.UploadedAt"/> preserved) rather than failing — the
/// browser may retry on a flaky connection and we don't want that to be a
/// user-visible error.
///
/// <para><b>Daemon staging handshake.</b> After stamping
/// <see cref="Attachment.UploadedAt"/> (and only on the first non-idempotent
/// call — already-staged attachments skip the push), we mint a fresh ~15min
/// presigned GET URL and push <see cref="StageAttachmentPayload"/> at the
/// runtime owning the parent conversation's branch via
/// <see cref="IRuntimeClient.StageAttachment"/>. The daemon downloads, writes
/// to the local FS path, and acks back via <c>RuntimeHub.ReportAttachmentStaged</c>
/// which stamps <see cref="Attachment.StagedAt"/>. The push is fire-and-forget;
/// if the runtime is offline the SignalR group is empty and the message is
/// dropped — the spec's "Upload finishes but runtime is offline" edge case
/// covers this (the frontend chip times out client-side after a while and
/// offers Retry).</para>
/// </summary>
public record CompleteAttachmentCommand(Guid AttachmentId) : ICommand<Result<AttachmentDetailResponse>>;

public class CompleteAttachmentCommandHandler
    : ICommandHandler<CompleteAttachmentCommand, Result<AttachmentDetailResponse>>
{
    private static readonly TimeSpan DownloadUrlTtl = TimeSpan.FromHours(24);

    // Short-lived URL for the daemon's one-shot download. 15 minutes is plenty
    // for any 50 MB file on any connection the daemon can reach R2 over, while
    // staying short enough that a leaked URL doesn't sit usable for hours.
    private static readonly TimeSpan StagingUrlTtl = TimeSpan.FromMinutes(15);

    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<CompleteAttachmentCommandHandler> _logger;

    public CompleteAttachmentCommandHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<CompleteAttachmentCommandHandler> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task<Result<AttachmentDetailResponse>> Handle(
        CompleteAttachmentCommand request,
        CancellationToken cancellationToken)
    {
        var attachment = await _db.Attachments
            .Include(a => a.Conversation)
            .FirstOrDefaultAsync(a => a.Id == request.AttachmentId, cancellationToken);

        if (attachment is null)
        {
            return Result.Failure<AttachmentDetailResponse>("Attachment not found");
        }

        // Track whether this call is the one that flipped UploadedAt from null
        // to a real timestamp. We only push the staging handshake on the
        // first-completion path — a retry on an already-complete row is a
        // no-op for the daemon. The daemon-side staging is also idempotent so
        // a double-push is harmless, but skipping the second push avoids
        // pointless wire traffic + log noise.
        var firstCompletion = attachment.UploadedAt is null;

        if (firstCompletion)
        {
            attachment.UploadedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Completed attachment {AttachmentId} for conversation {ConversationId}",
                attachment.Id, attachment.ConversationId);
        }
        else
        {
            _logger.LogInformation(
                "Attachment {AttachmentId} already complete; returning existing record (idempotent)",
                attachment.Id);
        }

        string downloadUrl;
        try
        {
            downloadUrl = await _fileStorage.GetPresignedGetUrlAsync(
                attachment.R2Key,
                DownloadUrlTtl,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate presigned GET URL for completed attachment {AttachmentId}",
                attachment.Id);
            return Result.Failure<AttachmentDetailResponse>("Failed to generate download URL");
        }

        // Daemon staging handshake — only on the first-completion path and only
        // when the attachment hasn't already been staged. The hub-side
        // ReportAttachmentStaged is the only writer of StagedAt, so checking
        // it here doubles as a guard against double-staging if a future caller
        // ever re-runs the complete handshake out of band.
        if (firstCompletion && attachment.StagedAt is null)
        {
            await TryPushStagingAsync(attachment, cancellationToken);
        }

        return Result.Success(new AttachmentDetailResponse
        {
            Id = attachment.Id,
            ConversationId = attachment.ConversationId,
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            SizeBytes = attachment.SizeBytes,
            DownloadUrl = downloadUrl,
            UploadedAt = attachment.UploadedAt,
            StagedAt = attachment.StagedAt,
            CreatedAt = attachment.CreatedAt,
        });
    }

    /// <summary>
    /// Best-effort push of the StageAttachment payload to the runtime that
    /// owns the parent conversation's branch. We resolve the runtime by
    /// <c>BranchId</c> (not <c>ProjectId</c>) because after CopyBranch a
    /// project can own multiple runtime rows — one per branch. Filtering by
    /// project alone would route to an arbitrary sibling runtime.
    ///
    /// <para>Any failure here is logged and swallowed: the upload-complete HTTP
    /// response must still succeed so the browser can update its chip from
    /// "Uploading" → "Staging on runtime". A failed push leaves the chip
    /// stuck in "Staging" until the frontend's client-side timeout flips it
    /// to "Runtime download failed" — see the spec's edge case "Upload
    /// finishes but runtime is offline".</para>
    /// </summary>
    private async Task TryPushStagingAsync(Attachment attachment, CancellationToken ct)
    {
        // Resolve the runtime via branch — same join shape TurnDispatcher uses.
        // AsNoTracking because we only need the id; the Attachment + Conversation
        // remain tracked in the DbContext.
        var runtimeId = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.BranchId == attachment.Conversation.BranchId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (runtimeId is null)
        {
            _logger.LogWarning(
                "CompleteAttachment: no runtime for branch {BranchId} (conversation {ConversationId}, attachment {AttachmentId}); skipping daemon staging push — chip will time out client-side.",
                attachment.Conversation.BranchId, attachment.ConversationId, attachment.Id);
            return;
        }

        string stagingUrl;
        try
        {
            stagingUrl = await _fileStorage.GetPresignedGetUrlAsync(
                attachment.R2Key,
                StagingUrlTtl,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CompleteAttachment: failed to mint staging URL for attachment {AttachmentId}; skipping daemon push.",
                attachment.Id);
            return;
        }

        var payload = new StageAttachmentPayload(
            AttachmentId: attachment.Id,
            ConversationId: attachment.ConversationId,
            FileName: attachment.FileName,
            DownloadUrl: stagingUrl,
            LocalPath: PromptPrefixBuilder.LocalPathFor(attachment));

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId.Value}")
                .StageAttachment(payload);
            _logger.LogInformation(
                "CompleteAttachment: pushed StageAttachment to runtime {RuntimeId} for attachment {AttachmentId} (conversation {ConversationId}).",
                runtimeId.Value, attachment.Id, attachment.ConversationId);
        }
        catch (Exception ex)
        {
            // Group push failures are best-effort. The chip will sit in
            // "Staging on runtime" until the frontend's timeout flips it.
            _logger.LogWarning(ex,
                "CompleteAttachment: StageAttachment push failed for attachment {AttachmentId} (runtime {RuntimeId}); chip will time out client-side.",
                attachment.Id, runtimeId.Value);
        }
    }
}

/// <summary>
/// Full shape returned to the client by the complete + get-by-id endpoints.
/// <see cref="DownloadUrl"/> is a freshly-minted presigned GET — the client
/// should treat it as short-lived (typically 24h) and re-fetch the record if
/// it goes stale.
/// </summary>
public record AttachmentDetailResponse
{
    public required Guid Id { get; init; }
    public required Guid ConversationId { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string DownloadUrl { get; init; }
    public DateTime? UploadedAt { get; init; }
    public DateTime? StagedAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
