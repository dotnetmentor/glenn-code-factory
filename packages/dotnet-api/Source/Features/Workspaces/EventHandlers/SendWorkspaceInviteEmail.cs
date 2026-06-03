using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Events;
using Source.Infrastructure;
using Source.Infrastructure.Services.Email;
using Source.Shared.Events;

namespace Source.Features.Workspaces.EventHandlers;

/// <summary>
/// Delivers the workspace invite to the recipient by emailing them the accept
/// link (<c>/invite/{token}</c>). Without this the invite row was created in the
/// DB but the invitee never received anything — the inviter saw "Invite sent"
/// while nothing actually went out, and the accept page was unreachable.
/// </summary>
public sealed class SendWorkspaceInviteEmailHandler : IEventHandler<WorkspaceInviteCreated>
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SendWorkspaceInviteEmailHandler> _logger;

    public SendWorkspaceInviteEmailHandler(
        ApplicationDbContext db,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<SendWorkspaceInviteEmailHandler> logger)
    {
        _db = db;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task Handle(WorkspaceInviteCreated notification, CancellationToken cancellationToken)
    {
        // Re-load the invite for the secret token (not carried on the event) and
        // the workspace for its display name.
        var invite = await _db.WorkspaceInvites
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == notification.InviteId, cancellationToken);
        if (invite is null)
        {
            _logger.LogWarning(
                "WorkspaceInviteCreated for {InviteId} but the invite row was not found; skipping email",
                notification.InviteId);
            return;
        }

        var workspace = await _db.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == notification.WorkspaceId, cancellationToken);
        var workspaceName = workspace?.Name ?? "a workspace";

        // Build the accept link. Prefer an absolute URL when App:FrontendBaseUrl
        // is configured; fall back to a relative path otherwise (mirrors the
        // password-reset email).
        var path = $"/invite/{Uri.EscapeDataString(invite.Token)}";
        var baseUrl = _configuration["App:FrontendBaseUrl"];
        var acceptLink = string.IsNullOrWhiteSpace(baseUrl)
            ? path
            : $"{baseUrl.TrimEnd('/')}{path}";

        var subject = $"You've been invited to {workspaceName} on GlennCode";
        var expires = invite.ExpiresAt.ToString("MMMM d, yyyy");

        var htmlBody = $@"
<div style='font-family: -apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
    <h2 style='color: #212529;'>You're invited to {workspaceName}</h2>
    <p style='color: #495057; line-height: 1.6;'>
        You've been invited to join the <strong>{workspaceName}</strong> workspace
        on GlennCode as a <strong>{invite.Role}</strong>. Click below to accept and get started.
    </p>
    <div style='margin: 30px 0;'>
        <a href='{acceptLink}' style='background: #1971c2; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-size: 16px; display: inline-block;'>
            Accept invitation
        </a>
    </div>
    <p style='color: #868e96; font-size: 14px; line-height: 1.6;'>
        This invitation expires on {expires}. If the button doesn't work, copy and paste this link into your browser:<br />
        <a href='{acceptLink}' style='color: #1971c2;'>{acceptLink}</a>
    </p>
    <p style='color: #adb5bd; font-size: 13px;'>
        If you weren't expecting this invitation, you can safely ignore this email.
    </p>
</div>";

        var textBody =
            $"You've been invited to join the {workspaceName} workspace on GlennCode as a {invite.Role}.\n\n" +
            $"Accept your invitation: {acceptLink}\n\n" +
            $"This invitation expires on {expires}.\n\n" +
            "If you weren't expecting this invitation, you can safely ignore this email.";

        try
        {
            await _emailService.SendEmailAsync(
                new EmailMessage(
                    To: invite.Email,
                    Subject: subject,
                    HtmlBody: htmlBody,
                    TextBody: textBody),
                cancellationToken);

            _logger.LogInformation(
                "Sent workspace invite email to {Email} for workspace {WorkspaceId} (invite {InviteId})",
                invite.Email, notification.WorkspaceId, notification.InviteId);
        }
        catch (Exception ex)
        {
            // Don't fail the surrounding transaction — the invite row is valid and
            // can be re-sent. Log loudly so a delivery failure is visible.
            _logger.LogError(ex,
                "Failed to send workspace invite email to {Email} for invite {InviteId}",
                invite.Email, notification.InviteId);
        }
    }
}
