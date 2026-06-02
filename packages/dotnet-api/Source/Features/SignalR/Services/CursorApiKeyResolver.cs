using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;

namespace Source.Features.SignalR.Services;

public sealed record CursorApiKeyStatus(
    bool HasProjectCursorApiKey,
    bool HasWorkspaceCursorApiKey,
    bool AllowProjectCursorApiKeyOverride,
    bool HasEffectiveCursorApiKey);

public interface ICursorApiKeyResolver
{
    Task<string?> ResolveForProjectAsync(Guid projectId, CancellationToken ct);

    Task<CursorApiKeyStatus> GetStatusForProjectAsync(Guid projectId, CancellationToken ct);
}

public sealed class CursorApiKeyResolver : ICursorApiKeyResolver
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ILogger<CursorApiKeyResolver> _logger;

    public CursorApiKeyResolver(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ILogger<CursorApiKeyResolver> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<string?> ResolveForProjectAsync(Guid projectId, CancellationToken ct)
    {
        var row = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                p.EncryptedCursorApiKey,
                p.WorkspaceId,
                WorkspaceKey = p.Workspace.EncryptedCursorApiKey,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return FromHostEnv();
        }

        var fromProject = await TryDecryptProjectEnvelopeAsync(projectId, row.EncryptedCursorApiKey, ct);
        if (!string.IsNullOrWhiteSpace(fromProject))
        {
            return fromProject;
        }

        var fromWorkspace = await TryDecryptWorkspaceEnvelopeAsync(
            row.WorkspaceId, row.WorkspaceKey, ct);
        if (!string.IsNullOrWhiteSpace(fromWorkspace))
        {
            return fromWorkspace;
        }

        return FromHostEnv();
    }

    public async Task<CursorApiKeyStatus> GetStatusForProjectAsync(Guid projectId, CancellationToken ct)
    {
        var row = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                HasProjectKey = p.EncryptedCursorApiKey != null && p.EncryptedCursorApiKey != "",
                p.Workspace.EncryptedCursorApiKey,
                p.Workspace.AllowProjectCursorApiKeyOverride,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return new CursorApiKeyStatus(false, false, true, false);
        }

        var hasWorkspaceKey = !string.IsNullOrWhiteSpace(row.EncryptedCursorApiKey);
        var hasEffective = row.HasProjectKey || hasWorkspaceKey;

        return new CursorApiKeyStatus(
            HasProjectCursorApiKey: row.HasProjectKey,
            HasWorkspaceCursorApiKey: hasWorkspaceKey,
            AllowProjectCursorApiKeyOverride: row.AllowProjectCursorApiKeyOverride,
            HasEffectiveCursorApiKey: hasEffective);
    }

    private async Task<string?> TryDecryptProjectEnvelopeAsync(
        Guid projectId,
        string? envelope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            return null;
        }

        try
        {
            var (ciphertext, nonce, dekVersion) = ProjectByokEnvelope.Unpack(envelope);
            var plaintext = await _encryption.DecryptAsync(projectId, ciphertext, nonce, dekVersion, ct);
            return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CursorApiKeyResolver: failed to decrypt project Cursor envelope for project {ProjectId}.",
                projectId);
            return null;
        }
    }

    private async Task<string?> TryDecryptWorkspaceEnvelopeAsync(
        Guid workspaceId,
        string? envelope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            return null;
        }

        try
        {
            var (ciphertext, nonce, dekVersion) = ProjectByokEnvelope.Unpack(envelope);
            var plaintext = await _encryption.DecryptForWorkspaceAsync(
                workspaceId, ciphertext, nonce, dekVersion, ct);
            return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CursorApiKeyResolver: failed to decrypt workspace Cursor envelope for workspace {WorkspaceId}.",
                workspaceId);
            return null;
        }
    }

    private static string? FromHostEnv()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
