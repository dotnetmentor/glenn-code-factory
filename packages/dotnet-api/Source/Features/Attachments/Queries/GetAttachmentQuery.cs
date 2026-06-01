using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Commands;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Attachments.Queries;

/// <summary>
/// Fetch a single attachment by id, returning the metadata + a fresh
/// short-lived presigned download URL. This is the endpoint past-message
/// attachment chips call when the user clicks them — the URL is regenerated
/// on every read so links don't go stale on long-lived sessions.
/// </summary>
public record GetAttachmentQuery(
    Guid AttachmentId,
    string CallerUserId,
    bool CallerIsSuperAdmin) : IQuery<Result<AttachmentDetailResponse>>;

public class GetAttachmentQueryHandler : IQueryHandler<GetAttachmentQuery, Result<AttachmentDetailResponse>>
{
    private static readonly TimeSpan DownloadUrlTtl = TimeSpan.FromHours(24);

    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<GetAttachmentQueryHandler> _logger;

    public GetAttachmentQueryHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage,
        ILogger<GetAttachmentQueryHandler> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public async Task<Result<AttachmentDetailResponse>> Handle(
        GetAttachmentQuery request,
        CancellationToken cancellationToken)
    {
        var attachment = await _db.Attachments
            .AsNoTracking()
            .Include(a => a.Conversation)
            .FirstOrDefaultAsync(a => a.Id == request.AttachmentId, cancellationToken);

        if (attachment is null)
        {
            return Result.Failure<AttachmentDetailResponse>("Attachment not found");
        }

        if (!await _db.UserCanAccessProjectAsync(
                request.CallerUserId,
                request.CallerIsSuperAdmin,
                attachment.Conversation.ProjectId,
                cancellationToken))
        {
            return Result.Failure<AttachmentDetailResponse>("Attachment not found");
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
                "Failed to generate presigned GET URL for attachment {AttachmentId}",
                attachment.Id);
            return Result.Failure<AttachmentDetailResponse>("Failed to generate download URL");
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
}
