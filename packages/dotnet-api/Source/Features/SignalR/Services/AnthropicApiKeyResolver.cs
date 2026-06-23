using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;

namespace Source.Features.SignalR.Services;

/// <summary>
/// Presence flags for the project's Anthropic API key, used by the BYOK
/// response so the settings UI can render the right "key set / not set" state
/// without ever echoing the plaintext. Mirrors <see cref="CursorApiKeyStatus"/>
/// but without the workspace tier — Anthropic BYOK is project-scoped only in
/// this slice (the host env var is the platform fallback).
/// </summary>
public sealed record AnthropicApiKeyStatus(
    bool HasProjectAnthropicApiKey,
    bool HasEffectiveAnthropicApiKey);

public interface IAnthropicApiKeyResolver
{
    /// <summary>
    /// Resolve the effective Anthropic API key for a project: per-project
    /// encrypted envelope → host <c>ANTHROPIC_API_KEY</c> env var. Returns
    /// <c>null</c> when no source supplies one.
    /// </summary>
    Task<string?> ResolveForProjectAsync(Guid projectId, CancellationToken ct);

    Task<AnthropicApiKeyStatus> GetStatusForProjectAsync(Guid projectId, CancellationToken ct);
}

/// <summary>
/// Project-scoped Anthropic API key resolver, the Claude-backend analogue of
/// <see cref="CursorApiKeyResolver"/>. Resolution order: the per-project
/// encrypted envelope on <c>Project.EncryptedAnthropicApiKey</c> (decrypted via
/// the project DEK) → the host <c>ANTHROPIC_API_KEY</c> environment variable.
/// No workspace tier and no SystemSettings tier in this slice — they slot in
/// the same way Cursor's would if a later card needs them.
/// </summary>
public sealed class AnthropicApiKeyResolver : IAnthropicApiKeyResolver
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ILogger<AnthropicApiKeyResolver> _logger;

    public AnthropicApiKeyResolver(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ILogger<AnthropicApiKeyResolver> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<string?> ResolveForProjectAsync(Guid projectId, CancellationToken ct)
    {
        var envelope = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.EncryptedAnthropicApiKey)
            .FirstOrDefaultAsync(ct);

        var fromProject = await TryDecryptProjectEnvelopeAsync(projectId, envelope, ct);
        if (!string.IsNullOrWhiteSpace(fromProject))
        {
            return fromProject;
        }

        return FromHostEnv();
    }

    public async Task<AnthropicApiKeyStatus> GetStatusForProjectAsync(Guid projectId, CancellationToken ct)
    {
        var envelope = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.EncryptedAnthropicApiKey)
            .FirstOrDefaultAsync(ct);

        var hasProject = !string.IsNullOrWhiteSpace(envelope);
        var hasEffective = hasProject || !string.IsNullOrWhiteSpace(FromHostEnv());

        return new AnthropicApiKeyStatus(
            HasProjectAnthropicApiKey: hasProject,
            HasEffectiveAnthropicApiKey: hasEffective);
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
                "AnthropicApiKeyResolver: failed to decrypt project Anthropic envelope for project {ProjectId}.",
                projectId);
            return null;
        }
    }

    private static string? FromHostEnv()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
