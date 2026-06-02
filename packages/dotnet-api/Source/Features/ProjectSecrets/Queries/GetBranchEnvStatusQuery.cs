using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;
using System.Text.RegularExpressions;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// Compute the env-var "readiness" status for a single branch: which env vars
/// the branch's currently-deployed runtime spec declares it <em>requires</em>,
/// which the branch actually <em>has</em> (branch-effective: project-wide rows
/// overlaid by branch-specific overrides), and therefore which required vars are
/// still <em>missing</em>.
///
/// <list type="bullet">
///   <item><b>Required</b> = the union of <see cref="ServiceSpec.RequiredEnv"/>
///         across every service in the branch's current expanded spec. The
///         current spec is resolved through
///         <see cref="ICurrentExpandedSpecResolver"/> so "required" matches what
///         is actually deployed (the most-recent terminal proposal's
///         <c>ExpandedSpec</c>), not a stale or hypothetical spec. A project with
///         no terminal proposal yet has an empty required set.</item>
///   <item><b>Present</b> = the branch-effective key set — project-wide rows
///         (<c>BranchId == null</c>) unioned with this branch's rows, branch
///         winning per key. Mirrors the bootstrap resolution in
///         <see cref="Controllers.BootstrapEnvController"/>.</item>
///   <item><b>Missing</b> = required keys absent from present.</item>
/// </list>
///
/// <para>Ownership / branch-belongs-to-project checks are the caller's
/// responsibility (the controller's gate). This handler assumes the
/// <paramref name="ProjectId"/> / <paramref name="BranchId"/> pair has already
/// been authorised.</para>
/// </summary>
public record GetBranchEnvStatusQuery(
    Guid ProjectId,
    Guid BranchId) : IQuery<Result<EnvStatusResponse>>;

/// <summary>
/// Status projection for a single required env var. <see cref="Satisfied"/> is
/// the per-key answer to "is this required var present on the branch?" — the
/// frontend renders a green / red indicator off it without re-deriving from the
/// <see cref="EnvStatusResponse.Missing"/> list.
/// </summary>
public record RequiredEnvStatusItem(
    string Service,
    string Key,
    string? Description,
    bool? Secret,
    bool Satisfied);

/// <summary>
/// An env var the spec declares but does not require before boot.
/// </summary>
public record SuggestedEnvStatusItem(
    string Service,
    string Key,
    string? Description,
    bool? Secret,
    bool Satisfied);

/// <summary>
/// Result of <see cref="GetBranchEnvStatusQuery"/>: the branch-effective present
/// keys, required/suggested status items, and the missing-required key list.
/// All arrays are sorted for deterministic responses.
/// </summary>
public record EnvStatusResponse(
    string[] Present,
    RequiredEnvStatusItem[] Required,
    SuggestedEnvStatusItem[] Suggested,
    string[] Missing,
    string[] Warnings);

public class GetBranchEnvStatusQueryHandler
    : IQueryHandler<GetBranchEnvStatusQuery, Result<EnvStatusResponse>>
{
    private const string DatabaseUrlKey = "DATABASE_URL";
    private const int DefaultPostgresPort = 5432;
    private const int LocalDockerPostgresPort = 43594;

    private readonly ApplicationDbContext _db;
    private readonly ICurrentExpandedSpecResolver _currentExpandedResolver;
    private readonly SecretEncryptionService _encryption;

    public GetBranchEnvStatusQueryHandler(
        ApplicationDbContext db,
        ICurrentExpandedSpecResolver currentExpandedResolver,
        SecretEncryptionService encryption)
    {
        _db = db;
        _currentExpandedResolver = currentExpandedResolver;
        _encryption = encryption;
    }

    public async Task<Result<EnvStatusResponse>> Handle(
        GetBranchEnvStatusQuery request,
        CancellationToken cancellationToken)
    {
        // -------- present: branch-effective key set --------
        // Project-wide (BranchId == null) overlaid by this branch's rows; branch
        // wins per key. Same resolution as BootstrapEnvController, but we only
        // need the KEY set here — no decryption. The global query filter excludes
        // soft-deleted rows.
        var rows = await _db.ProjectSecrets
            .AsNoTracking()
            .Where(s => s.ProjectId == request.ProjectId
                && (s.BranchId == null || s.BranchId == request.BranchId))
            .Select(s => new { s.Key, s.BranchId })
            .ToListAsync(cancellationToken);

        var presentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // A branch-specific row OR a project-wide row both contribute the key
            // to "present" — for the present set we only care that the key exists
            // at either scope, so a plain union is correct.
            presentKeys.Add(row.Key);
        }

        // -------- required: union of RequiredEnv across the current spec --------
        var currentExpandedJson = await _currentExpandedResolver.ResolveAsync(
            request.ProjectId, excludeProposalId: null, cancellationToken);

        var requiredItems = new List<RequiredEnvStatusItem>();
        var suggestedItems = new List<SuggestedEnvStatusItem>();
        List<ServiceSpec>? expandedServices = null;
        if (!string.IsNullOrWhiteSpace(currentExpandedJson))
        {
            var parsed = RuntimeSpecV2.TryParse(currentExpandedJson);
            if (parsed.IsSuccess && parsed.Value.Services is { Count: > 0 } services)
            {
                expandedServices = services;
                // Dedupe by key — the same var can be declared by multiple
                // services. We keep the FIRST declaring service name (simple,
                // deterministic given spec order) and skip later dupes.
                var seenRequired = new HashSet<string>(StringComparer.Ordinal);
                var seenSuggested = new HashSet<string>(StringComparer.Ordinal);
                foreach (var service in services)
                {
                    if (service.RequiredEnv is not { Count: > 0 } reqs)
                    {
                        continue;
                    }

                    foreach (var req in reqs)
                    {
                        if (string.IsNullOrEmpty(req.Key))
                        {
                            continue;
                        }

                        var satisfied = presentKeys.Contains(req.Key);
                        if (req.IsRequired)
                        {
                            if (!seenRequired.Add(req.Key))
                            {
                                continue;
                            }

                            requiredItems.Add(new RequiredEnvStatusItem(
                                Service: service.Name,
                                Key: req.Key,
                                Description: req.Description,
                                Secret: req.Secret,
                                Satisfied: satisfied));
                        }
                        else
                        {
                            if (!seenSuggested.Add(req.Key))
                            {
                                continue;
                            }

                            suggestedItems.Add(new SuggestedEnvStatusItem(
                                Service: service.Name,
                                Key: req.Key,
                                Description: req.Description,
                                Secret: req.Secret,
                                Satisfied: satisfied));
                        }
                    }
                }
            }
        }

        // -------- missing: required keys absent from present --------
        var missing = requiredItems
            .Where(r => !r.Satisfied)
            .Select(r => r.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var present = presentKeys
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        var required = requiredItems
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();

        var suggested = suggestedItems
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();

        var warnings = await BuildWarningsAsync(
            request,
            presentKeys,
            expandedServices,
            cancellationToken);

        return Result.Success(new EnvStatusResponse(present, required, suggested, missing, warnings));
    }

    private async Task<string[]> BuildWarningsAsync(
        GetBranchEnvStatusQuery request,
        IReadOnlySet<string> presentKeys,
        List<ServiceSpec>? expandedServices,
        CancellationToken cancellationToken)
    {
        if (expandedServices is null
            || !presentKeys.Contains(DatabaseUrlKey)
            || !expandedServices.Any(IsPostgresService))
        {
            return [];
        }

        var expectedPort = ResolvePostgresPort(expandedServices);
        var databaseUrl = await ResolveEffectivePlaintextAsync(
            request.ProjectId,
            request.BranchId,
            DatabaseUrlKey,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            return [];
        }

        var configuredPort = TryParseConnectionStringPort(databaseUrl);
        if (configuredPort is null)
        {
            return [];
        }

        if (configuredPort == LocalDockerPostgresPort
            || (expectedPort != LocalDockerPostgresPort && configuredPort != expectedPort))
        {
            return
            [
                $"DATABASE_URL uses Port={configuredPort}, but this runtime's Postgres listens on {expectedPort}. "
                + "Port 43594 is the local Docker dev default — update DATABASE_URL before booting the runtime.",
            ];
        }

        return [];
    }

    private async Task<string?> ResolveEffectivePlaintextAsync(
        Guid projectId,
        Guid branchId,
        string key,
        CancellationToken cancellationToken)
    {
        var candidates = await _db.ProjectSecrets
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId
                && s.Key == key
                && (s.BranchId == null || s.BranchId == branchId))
            .ToListAsync(cancellationToken);

        var secret =
            candidates.FirstOrDefault(s => s.BranchId == branchId)
            ?? candidates.FirstOrDefault(s => s.BranchId == null);
        if (secret is null)
        {
            return null;
        }

        return await _encryption.DecryptAsync(
            projectId,
            secret.Ciphertext,
            secret.Nonce,
            secret.DekVersion,
            cancellationToken);
    }

    private static bool IsPostgresService(ServiceSpec service)
    {
        if (service.Name.Contains("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return service.Command.Contains("postgres", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolvePostgresPort(IEnumerable<ServiceSpec> services)
    {
        foreach (var service in services.Where(IsPostgresService))
        {
            var fromCommand = Regex.Match(service.Command, @"-p\s+(\d+)");
            if (fromCommand.Success && int.TryParse(fromCommand.Groups[1].Value, out var commandPort))
            {
                return commandPort;
            }

            if (service.Env?.TryGetValue("PGPORT", out var pgPort) == true
                && int.TryParse(pgPort, out var envPort))
            {
                return envPort;
            }
        }

        return DefaultPostgresPort;
    }

    private static int? TryParseConnectionStringPort(string connectionString)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (!trimmed.StartsWith("Port=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed["Port=".Length..];
            return int.TryParse(value, out var port) ? port : null;
        }

        return null;
    }
}
