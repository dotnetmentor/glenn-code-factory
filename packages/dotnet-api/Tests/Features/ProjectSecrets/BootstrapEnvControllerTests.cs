using System.Security.Claims;
using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.ProjectSecrets.Controllers;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Unit tests for <see cref="BootstrapEnvController.GetBootstrapEnv"/> — the
/// daemon-facing cold-boot env-bundle endpoint. We instantiate the controller
/// directly against a real in-memory <see cref="ApplicationDbContext"/> and
/// stamp a <see cref="DefaultHttpContext"/> pre-populated with a
/// <see cref="ClaimsPrincipal"/> carrying the <c>rt_runtime</c> claim — i.e.
/// we test what the controller does *after* the JWT bearer middleware has
/// authenticated the caller. No HTTP pipeline, no SignalR.
///
/// <para>Mirrors <see cref="Api.Tests.Features.Conversations.GetActiveSessionForRuntimeTests"/>
/// — same controller-level claim cross-check pattern, same in-memory rig.</para>
///
/// <para><b>What this file does NOT cover:</b> the JWT bearer middleware itself
/// (missing/expired/revoked → 401). That wiring lives in
/// <c>AuthenticationExtensions.AddRuntimeTokenAuthScheme</c> and is exercised
/// indirectly by <c>RuntimeTokenServiceTests</c> + the revocation cache tests.
/// 401 here is purely middleware-level — re-asserting it would just re-test
/// JwtBearerHandler.</para>
/// </summary>
public class BootstrapEnvControllerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApplicationDbContext OpenDb() => SecretsTestHarness.OpenDb(_dbName);

    /// <summary>
    /// Build a controller whose <see cref="ControllerBase.User"/> carries the
    /// supplied claim runtime id. <paramref name="claimRuntimeId"/> = null
    /// simulates "authenticated principal but no rt_runtime claim".
    /// </summary>
    private BootstrapEnvController CreateController(
        Guid? claimRuntimeId,
        SecretEncryptionService encryption,
        ApplicationDbContext db)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (claimRuntimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, claimRuntimeId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };

        return new BootstrapEnvController(
            db, encryption, NullLogger<BootstrapEnvController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(Guid projectId, bool isDeleted = false)
    {
        await using var db = OpenDb();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Region = "arn",
            IsDeleted = isDeleted,
        };
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Encrypt + persist a <see cref="ProjectSecret"/> via the same encryption
    /// service the controller uses, so the controller's decrypt loop produces
    /// the original plaintext.
    /// </summary>
    private async Task SeedSecretAsync(
        SecretEncryptionService encryption,
        Guid projectId,
        string key,
        string plaintext)
    {
        var (ciphertext, nonce, dekVersion) = await encryption.EncryptAsync(
            projectId, plaintext, CancellationToken.None);

        await using var db = OpenDb();
        db.ProjectSecrets.Add(new ProjectSecret
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Key = key,
            Ciphertext = ciphertext,
            Nonce = nonce,
            DekVersion = dekVersion,
            Version = 1,
            CreatedBy = "test-user",
        });
        await db.SaveChangesAsync();
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ClaimMismatchesPath_Returns403()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var runtime = await SeedRuntimeAsync(Guid.NewGuid());

        // Claim carries a different (still-valid) Guid — a daemon may only ask
        // about itself. 403, not 401: the caller IS authenticated, just
        // unauthorised for this resource.
        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: Guid.NewGuid(), encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ClaimMissing_Returns403()
    {
        // Authenticated principal but with NO rt_runtime claim — the auth
        // scheme would reject this in production (a valid RuntimeToken always
        // carries rt_runtime), but the controller still defends against a
        // malformed principal and returns 403 (not 401: a missing token is
        // a middleware concern, this is a malformed-claim concern).
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var runtime = await SeedRuntimeAsync(Guid.NewGuid());

        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: null, encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task RuntimeNotFound_Returns404()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var unknownRuntimeId = Guid.NewGuid();

        // Claim matches the path id, but no runtime row exists.
        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: unknownRuntimeId, encryption, db);

        var result = await controller.GetBootstrapEnv(unknownRuntimeId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SoftDeletedRuntime_Returns404()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var runtime = await SeedRuntimeAsync(Guid.NewGuid(), isDeleted: true);

        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: runtime.Id, encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "the global query filter on ProjectRuntime hides IsDeleted=true rows; a torn-down runtime has no bootstrap bundle");
    }

    [Fact]
    public async Task EmptySecrets_Returns200_WithEmptyArray()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);

        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: runtime.Id, encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapEnvResponse>().Subject;
        payload.Entries.Should().NotBeNull();
        payload.Entries.Should().BeEmpty(
            "a project with no secrets returns 200 with []; the daemon treats that as 'wipe the env file' rather than an error");

        // Audit row still written — even an empty bundle is a delivery event.
        await using var verify = OpenDb();
        var audit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.BootstrapDelivered);
        audit.Actor.Should().Be("system:bootstrap");
        audit.ProjectId.Should().Be(projectId);
        audit.SecretId.Should().BeNull();
        audit.SecretKey.Should().BeNull();
    }

    [Fact]
    public async Task MultipleSecrets_Returns200_WithAllDecrypted_AndAuditRowWritten()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);

        await SeedSecretAsync(encryption, projectId, "STRIPE_API_KEY", "sk_live_abc");
        await SeedSecretAsync(encryption, projectId, "OPENAI_API_KEY", "sk-openai-xyz");
        await SeedSecretAsync(encryption, projectId, "DATABASE_URL", "postgres://localhost/db");

        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: runtime.Id, encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapEnvResponse>().Subject;
        payload.Entries.Should().HaveCount(3);

        // Order is by Key asc — assert both the keys and the round-tripped plaintexts.
        payload.Entries.Select(e => e.Key)
            .Should().ContainInOrder("DATABASE_URL", "OPENAI_API_KEY", "STRIPE_API_KEY");

        var byKey = payload.Entries.ToDictionary(e => e.Key, e => e.Value);
        byKey["STRIPE_API_KEY"].Should().Be("sk_live_abc");
        byKey["OPENAI_API_KEY"].Should().Be("sk-openai-xyz");
        byKey["DATABASE_URL"].Should().Be("postgres://localhost/db");

        // ONE audit row per call — not per-key.
        await using var verify = OpenDb();
        var auditRows = await verify.SecretAuditEvents
            .Where(a => a.Action == SecretAuditAction.BootstrapDelivered)
            .ToListAsync();
        auditRows.Should().HaveCount(1, "one audit row per bootstrap call, not per-key");

        var audit = auditRows[0];
        audit.Actor.Should().Be("system:bootstrap");
        audit.ProjectId.Should().Be(projectId);
        audit.SecretId.Should().BeNull("the bootstrap delivery is a project-scoped event, not a per-secret reveal");
        audit.SecretKey.Should().BeNull("we never record secret keys in the bootstrap audit metadata");
    }

    [Fact]
    public async Task AuditRowMetadata_ContainsRuntimeIdAndDeliveredCount()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);

        await SeedSecretAsync(encryption, projectId, "ONE", "1");
        await SeedSecretAsync(encryption, projectId, "TWO", "2");

        await using var db = OpenDb();
        var controller = CreateController(claimRuntimeId: runtime.Id, encryption, db);

        var result = await controller.GetBootstrapEnv(runtime.Id, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();

        await using var verify = OpenDb();
        var audit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.BootstrapDelivered);

        audit.Metadata.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(audit.Metadata!);
        var root = doc.RootElement;

        root.GetProperty("runtimeId").GetString()
            .Should().Be(runtime.Id.ToString(), "metadata records the daemon's runtime id");
        root.GetProperty("deliveredCount").GetInt32()
            .Should().Be(2, "metadata records the count, not the keys themselves");
    }
}
