using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Attachments.Commands;
using Source.Features.Attachments.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Shared.Controllers;

namespace Source.Features.Attachments.Controllers;

/// <summary>
/// HTTP surface over chat-file-attachments. See spec
/// <c>chat-file-attachments</c>. Three endpoints:
///
/// <list type="bullet">
///   <item><c>POST /api/attachments/presign</c> — backend mints a short-lived
///         PUT URL the browser uses to upload bytes directly to R2. Backend
///         never proxies the body (see the spec's "Direct browser-to-storage
///         upload" architectural guideline).</item>
///   <item><c>POST /api/attachments/{id}/complete</c> — browser confirms the
///         PUT succeeded; backend stamps <c>UploadedAt</c> and returns the
///         full attachment record with a fresh download URL. Idempotent.</item>
///   <item><c>GET /api/attachments/{id}</c> — past-message chips call this
///         to re-open files. Backend mints a fresh ~24h presigned GET URL
///         each time so chat history stays usable indefinitely.</item>
/// </list>
///
/// <para><b>Daemon staging</b> (the <c>StagedAt</c> timestamp + the per-runtime
/// file copy) is wired by a follow-up card. This controller persists the
/// upload-completion handshake only.</para>
///
/// <para><b>Auth.</b> JWT plus per-project access via
/// <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> on the parent
/// conversation's project (SuperAdmin, owner, or workspace member). Missing
/// rows and denied access both surface as <c>404</c>.</para>
/// </summary>
[ApiController]
[Authorize]
[Tags("Attachments")]
[Route("api/attachments")]
public class AttachmentsController : BaseApiController
{
    private readonly ApplicationDbContext _db;

    public AttachmentsController(
        IMediator mediator,
        ILogger<AttachmentsController> logger,
        ApplicationDbContext db)
        : base(mediator, logger)
    {
        _db = db;
    }

    /// <summary>
    /// Request body for <c>POST /api/attachments/presign</c>.
    /// </summary>
    public record PresignAttachmentRequest(
        Guid ConversationId,
        string FileName,
        string? ContentType,
        long SizeBytes);

    /// <summary>
    /// Issue a presigned PUT URL the browser uploads directly to. Creates the
    /// attachment row in its pre-upload state and returns its id so the
    /// follow-up <c>/complete</c> call knows what to mark.
    /// </summary>
    [HttpPost("presign")]
    [ProducesResponseType(typeof(PresignAttachmentResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PresignAttachmentResponse>> Presign(
        [FromBody] PresignAttachmentRequest body,
        CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        var projectId = await _db.FindProjectIdForConversationAsync(body.ConversationId, ct);
        if (projectId is null || !await _db.CallerCanAccessProjectAsync(User, projectId.Value, ct))
        {
            return NotFound();
        }

        var command = new PresignAttachmentCommand(
            body.ConversationId,
            body.FileName,
            body.ContentType,
            body.SizeBytes);

        var result = await Mediator.Send(command, ct);
        return HandleResult(result);
    }

    /// <summary>
    /// Mark an attachment as uploaded. Idempotent — a retry on an already-
    /// complete attachment returns the same shape with the existing
    /// <c>UploadedAt</c> preserved. Daemon staging is wired by a follow-up card.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(AttachmentDetailResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<AttachmentDetailResponse>> Complete(
        Guid id,
        CancellationToken ct)
    {
        var projectId = await _db.FindProjectIdForAttachmentAsync(id, ct);
        if (projectId is null || !await _db.CallerCanAccessProjectAsync(User, projectId.Value, ct))
        {
            return NotFound();
        }

        var result = await Mediator.Send(new CompleteAttachmentCommand(id), ct);
        return HandleResultWithNotFound(result);
    }

    /// <summary>
    /// Fetch attachment metadata + a freshly-minted presigned download URL.
    /// Used by past-message attachment chips so users can re-open files
    /// months after the original send.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AttachmentDetailResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<AttachmentDetailResponse>> Get(
        Guid id,
        CancellationToken ct)
    {
        var projectId = await _db.FindProjectIdForAttachmentAsync(id, ct);
        if (projectId is null || !await _db.CallerCanAccessProjectAsync(User, projectId.Value, ct))
        {
            return NotFound();
        }

        var result = await Mediator.Send(new GetAttachmentQuery(id), ct);
        return HandleResultWithNotFound(result);
    }
}
