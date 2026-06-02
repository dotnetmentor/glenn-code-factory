using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Models;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Attachments.Commands;

/// <summary>
/// Issue a short-lived presigned PUT URL the browser can upload bytes to
/// directly — backend never proxies the file body. Mirrors the architectural
/// guideline from the chat-file-attachments spec ("Direct browser-to-storage
/// upload").
///
/// <para>Creates the <see cref="Attachment"/> row in its pre-upload state
/// (<see cref="Attachment.UploadedAt"/> = null) so the row id can be returned
/// to the client up-front. The client embeds that id when it later calls
/// <c>/api/attachments/{id}/complete</c>. The row exists even if the upload
/// is abandoned — periodic cleanup of orphan rows is a future concern, the
/// chat-file-attachments spec explicitly accepts abandoned bytes as a
/// non-user-facing tidy-up problem.</para>
///
/// <para>Validation:</para>
/// <list type="bullet">
///   <item>Filename must be non-empty after trim.</item>
///   <item>Size must be strictly &gt; 0 and ≤ <see cref="Attachment.MaxSizeBytes"/> (50 MiB).</item>
///   <item>Conversation must exist (resolved via DbContext — caller-level
///         ownership gating happens in the controller).</item>
/// </list>
/// </summary>
public record PresignAttachmentCommand(
    Guid ConversationId,
    string FileName,
    string? ContentType,
    long SizeBytes) : ICommand<Result<PresignAttachmentResponse>>;

/// <summary>
/// Response from <see cref="PresignAttachmentCommand"/>. The browser uses
/// <see cref="UploadUrl"/> to PUT the file bytes; <see cref="AttachmentId"/>
/// is the handle for the follow-up complete + get-by-id calls; <see cref="Key"/>
/// is the storage key (informational — the URL is the only thing the browser
/// needs).
/// </summary>
public record PresignAttachmentResponse
{
    public required Guid AttachmentId { get; init; }
    public required string UploadUrl { get; init; }
    public required string Key { get; init; }
}

public class PresignAttachmentCommandHandler
    : ICommandHandler<PresignAttachmentCommand, Result<PresignAttachmentResponse>>
{
    // 15 minutes is a sensible PUT window — plenty for a 50 MB upload on a
    // slow connection, short enough that a leaked URL doesn't sit usable for
    // hours.
    private static readonly TimeSpan UploadUrlTtl = TimeSpan.FromMinutes(15);

    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<PresignAttachmentCommandHandler> _logger;

    public PresignAttachmentCommandHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage,
        ILogger<PresignAttachmentCommandHandler> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public async Task<Result<PresignAttachmentResponse>> Handle(
        PresignAttachmentCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result.Failure<PresignAttachmentResponse>("File name is required");
        }

        var trimmedName = request.FileName.Trim();
        if (trimmedName.Length > Attachment.MaxFileNameLength)
        {
            return Result.Failure<PresignAttachmentResponse>(
                $"File name too long (max {Attachment.MaxFileNameLength} chars)");
        }

        if (request.SizeBytes <= 0)
        {
            return Result.Failure<PresignAttachmentResponse>("File size must be greater than 0");
        }

        if (request.SizeBytes > Attachment.MaxSizeBytes)
        {
            var maxMb = Attachment.MaxSizeBytes / (1024 * 1024);
            return Result.Failure<PresignAttachmentResponse>(
                $"File too large (max {maxMb} MB)");
        }

        var conversationExists = await _db.Conversations
            .IgnoreQueryFilters()
            .AnyAsync(c => c.Id == request.ConversationId, cancellationToken);

        if (!conversationExists)
        {
            return Result.Failure<PresignAttachmentResponse>("Conversation not found");
        }

        var attachmentId = Guid.NewGuid();
        var safeFileName = SanitizeFileName(trimmedName);
        var key = $"attachments/{request.ConversationId}/{attachmentId}-{safeFileName}";

        var attachment = new Attachment
        {
            Id = attachmentId,
            ConversationId = request.ConversationId,
            FileName = trimmedName,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            R2Key = key,
            UploadedAt = null,
            StagedAt = null,
        };

        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        string uploadUrl;
        try
        {
            uploadUrl = await _fileStorage.GetPresignedPutUrlAsync(
                key,
                request.ContentType,
                UploadUrlTtl,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate presigned PUT URL for attachment {AttachmentId} (key {Key})",
                attachmentId, key);
            return Result.Failure<PresignAttachmentResponse>("Failed to generate upload URL");
        }

        _logger.LogInformation(
            "Presigned attachment {AttachmentId} for conversation {ConversationId} ({SizeBytes} bytes, key {Key})",
            attachmentId, request.ConversationId, request.SizeBytes, key);

        return Result.Success(new PresignAttachmentResponse
        {
            AttachmentId = attachmentId,
            UploadUrl = uploadUrl,
            Key = key,
        });
    }

    /// <summary>
    /// Strip path separators and control characters from the filename so the
    /// storage key stays predictable / shell-safe. We keep the visible filename
    /// on the entity unsanitised (that's what the UI shows back to the user);
    /// only the on-disk / on-R2 key gets the safe variant.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        // Remove invalid path chars + slashes + control chars. Then collapse
        // whitespace runs to a single hyphen so the key is one token.
        var invalidChars = Path.GetInvalidFileNameChars()
            .Append('/')
            .Append('\\')
            .Distinct()
            .ToArray();

        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) || char.IsControl(c) ? '_' : c)
            .ToArray())
            .Trim();

        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "file";
        }

        // Hard-cap the storage filename segment so the full key never exceeds
        // the R2Key column (1024 chars). Conversation id (36) + attachment id
        // (36) + "attachments/" + 2 separators + "-" ≈ 90 chars of overhead;
        // 256 leaves comfortable headroom.
        if (sanitized.Length > 256)
        {
            var extension = Path.GetExtension(sanitized);
            var stem = Path.GetFileNameWithoutExtension(sanitized);
            var keep = Math.Max(1, 256 - extension.Length);
            sanitized = stem.Substring(0, Math.Min(stem.Length, keep)) + extension;
        }

        return sanitized;
    }
}
