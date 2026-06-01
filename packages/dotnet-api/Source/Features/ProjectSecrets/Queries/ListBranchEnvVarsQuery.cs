using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// List the branch-effective env vars for a single branch: project-wide rows
/// (<c>BranchId == null</c>) overlaid by this branch's rows, branch winning per
/// key. Same branch-effective resolution as
/// <see cref="Controllers.BootstrapEnvController"/>, but shaped for the editor UI
/// rather than the daemon's bootstrap bundle.
///
/// <para><b>Plaintext exposure.</b> The decrypted <see cref="BranchEnvVarItem.Value"/>
/// is populated ONLY for non-secret rows (<c>IsSecret == false</c>) — plain
/// config values are safe to render inline in the list. Secret rows leave
/// <see cref="BranchEnvVarItem.Value"/> <c>null</c>; their plaintext is reachable
/// only through the dedicated, audited reveal endpoint. This keeps the high-volume
/// list path from leaking secret material while still letting the UI show
/// non-sensitive config without a per-row reveal round-trip.</para>
///
/// <para>No audit row — listing is high-volume and (for the secret rows) leaks no
/// secret material. Mirrors <see cref="ListSecretsQuery"/>.</para>
/// </summary>
public record ListBranchEnvVarsQuery(
    Guid ProjectId,
    Guid BranchId) : IQuery<Result<List<BranchEnvVarItem>>>;

/// <summary>
/// One branch-effective env var for the editor list. <see cref="Scope"/> tells the
/// UI whether the effective value comes from a project-wide default
/// (<c>"project"</c>) or a branch-specific override (<c>"branch"</c>) so it can
/// render an "overridden" badge and gate the right edit affordances.
/// <see cref="Value"/> carries the decrypted plaintext for non-secret rows only;
/// it is <c>null</c> for secrets.
/// </summary>
public record BranchEnvVarItem(
    string Key,
    bool IsSecret,
    int Version,
    DateTime UpdatedAt,
    string Scope,
    string? Value);

public class ListBranchEnvVarsQueryHandler
    : IQueryHandler<ListBranchEnvVarsQuery, Result<List<BranchEnvVarItem>>>
{
    private const string ScopeProject = "project";
    private const string ScopeBranch = "branch";

    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;

    public ListBranchEnvVarsQueryHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<Result<List<BranchEnvVarItem>>> Handle(
        ListBranchEnvVarsQuery request,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.ProjectSecrets
            .Where(s => s.ProjectId == request.ProjectId
                && (s.BranchId == null || s.BranchId == request.BranchId))
            .ToListAsync(cancellationToken);

        // Branch-effective overlay keyed by env-var name: project-wide defaults
        // first, then branch-specific rows override. A row with a non-null
        // BranchId always wins over the project-wide (null) row.
        var effective = new Dictionary<string, ProjectSecret>(StringComparer.Ordinal);
        foreach (var secret in candidates)
        {
            if (secret.BranchId is null)
            {
                if (!effective.ContainsKey(secret.Key))
                {
                    effective[secret.Key] = secret;
                }
            }
            else
            {
                effective[secret.Key] = secret;
            }
        }

        var items = new List<BranchEnvVarItem>(effective.Count);
        foreach (var secret in effective.Values.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // Plaintext is exposed ONLY for non-secret rows. Secrets stay masked
            // here; the reveal endpoint is the single audited surface for them.
            string? value = null;
            if (!secret.IsSecret)
            {
                value = await _encryption.DecryptAsync(
                    request.ProjectId,
                    secret.Ciphertext,
                    secret.Nonce,
                    secret.DekVersion,
                    cancellationToken);
            }

            items.Add(new BranchEnvVarItem(
                Key: secret.Key,
                IsSecret: secret.IsSecret,
                Version: secret.Version,
                UpdatedAt: secret.UpdatedAt,
                Scope: secret.BranchId is null ? ScopeProject : ScopeBranch,
                Value: value));
        }

        return Result.Success(items);
    }
}
