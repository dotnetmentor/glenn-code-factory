using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;

namespace Source.Features.SignalR.Services;

/// <summary>
/// Resolves the Cursor SDK API key for a project: per-project encrypted envelope → host env var.
/// </summary>
public interface IAgentSecretsResolver
{
    Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct);
}

public sealed class AgentSecretsResolver : IAgentSecretsResolver
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ILogger<AgentSecretsResolver> _logger;

    public AgentSecretsResolver(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ILogger<AgentSecretsResolver> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<string?> ResolveCursorApiKeyAsync(Guid projectId, CancellationToken ct)
    {
        var envelope = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.EncryptedCursorApiKey)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(envelope))
        {
            try
            {
                var (ciphertext, nonce, dekVersion) = ProjectByokEnvelope.Unpack(envelope);
                var plaintext = await _encryption.DecryptAsync(projectId, ciphertext, nonce, dekVersion, ct);
                if (!string.IsNullOrWhiteSpace(plaintext))
                {
                    return plaintext;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AgentSecretsResolver: failed to decrypt Cursor envelope for project {ProjectId}; falling back to env var.",
                    projectId);
            }
        }

        var fromEnv = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
