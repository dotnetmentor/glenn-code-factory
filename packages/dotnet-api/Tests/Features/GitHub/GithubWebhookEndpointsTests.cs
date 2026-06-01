using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// End-to-end tests for the public GitHub webhook receiver
/// (<c>POST /api/github/webhooks</c>). The HMAC validator runs unmocked — tests sign their
/// own bodies with the same secret the host reads from <see cref="GithubOptions"/>.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GithubWebhookEndpointsTests : IntegrationTestBase
{
    private const string WebhookSecret = "test-webhook-secret-for-receiver-tests";

    public GithubWebhookEndpointsTests()
    {
        // Webhook secret is seeded into SystemSettings on first use rather than via IOptions.
        // SeedAsync is invoked lazily via EnsureWebhookSecretSeeded so it can run in the
        // test host's DI scope (constructor runs before the WebApplicationFactory is built).
    }

    private bool _webhookSecretSeeded;

    private async Task EnsureWebhookSecretSeededAsync()
    {
        if (_webhookSecretSeeded) return;
        await SeedGithubSystemSettingsAsync(new GithubOptions { WebhookSecret = WebhookSecret });
        _webhookSecretSeeded = true;
    }

    // -----------------------------------------------------------------------
    // Header / signature contract
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Returns_400_when_required_headers_missing()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // No headers attached at all.
        var response = await Client.PostAsync("/api/github/webhooks", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_400_when_only_event_header_present()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("X-GitHub-Event", "push");

        var response = await Client.PostAsync("/api/github/webhooks", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_401_for_invalid_signature()
    {
        var body = Encoding.UTF8.GetBytes(@"{""hello"":""world""}");
        var response = await PostWebhookAsync(
            eventName: "push",
            deliveryId: Guid.NewGuid().ToString(),
            body: body,
            signatureOverride: "sha256=" + new string('0', 64)); // syntactically valid, semantically wrong.

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accepts_unknown_event_with_valid_signature()
    {
        var body = Encoding.UTF8.GetBytes(@"{""action"":""created"",""thing"":42}");
        var deliveryId = Guid.NewGuid().ToString();

        var response = await PostWebhookAsync(
            eventName: "marketplace_purchase",
            deliveryId: deliveryId,
            body: body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Delivery row was persisted regardless of whether we routed it.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.GithubWebhookDeliveries.AnyAsync(d => d.DeliveryId == deliveryId)).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Idempotency
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_delivery_id_does_not_persist_twice()
    {
        var body = Encoding.UTF8.GetBytes(@"{""action"":""created""}");
        var deliveryId = Guid.NewGuid().ToString();

        var first = await PostWebhookAsync(eventName: "ping", deliveryId: deliveryId, body: body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PostWebhookAsync(eventName: "ping", deliveryId: deliveryId, body: body);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.GithubWebhookDeliveries.Where(d => d.DeliveryId == deliveryId).ToListAsync();
        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task Duplicate_delivery_does_not_redispatch_installation_handler()
    {
        // Seed the install row so the second installation_repositories.added call would visibly
        // alter DB state if the handler were re-invoked.
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 1234L, login: "octo-org");

        var deliveryId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "added",
            installation = new { id = 1234L },
            repositories_added = new[]
            {
                new { id = 9001L, name = "alpha", full_name = "octo-org/alpha", @private = false }
            },
            repositories_removed = Array.Empty<object>(),
        });

        var first = await PostWebhookAsync("installation_repositories", deliveryId, payload);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now manually delete the repo we just added — if the duplicate redispatched the handler,
        // it would re-create the row.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.GithubRepositories
                .SingleAsync(r => r.GithubInstallationId == inst.Id && r.GithubRepoId == 9001L);
            db.GithubRepositories.Remove(row);
            await db.SaveChangesAsync();
        }

        var second = await PostWebhookAsync("installation_repositories", deliveryId, payload);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stillRemoved = !await verifyDb.GithubRepositories
            .AnyAsync(r => r.GithubInstallationId == inst.Id && r.GithubRepoId == 9001L);
        stillRemoved.Should().BeTrue("a duplicate delivery must NOT re-run the dispatch path");
    }

    // -----------------------------------------------------------------------
    // installation_repositories
    // -----------------------------------------------------------------------

    [Fact]
    public async Task installation_repositories_added_upserts_repo_rows()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 4242L, login: "octo");

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "added",
            installation = new { id = 4242L },
            repositories_added = new[]
            {
                new { id = 1L, name = "one", full_name = "octo/one", @private = false },
                new { id = 2L, name = "two", full_name = "octo/two", @private = true },
            },
        });

        var response = await PostWebhookAsync("installation_repositories", Guid.NewGuid().ToString(), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.GithubRepositories
            .Where(r => r.GithubInstallationId == inst.Id)
            .OrderBy(r => r.GithubRepoId)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].FullName.Should().Be("octo/one");
        rows[0].Owner.Should().Be("octo");
        rows[0].Private.Should().BeFalse();
        rows[1].FullName.Should().Be("octo/two");
        rows[1].Private.Should().BeTrue();
    }

    [Fact]
    public async Task installation_repositories_removed_deletes_repo_rows()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 7777L, login: "octo");
        await SeedRepositoryAsync(inst, repoId: 11L, fullName: "octo/keep");
        await SeedRepositoryAsync(inst, repoId: 22L, fullName: "octo/gone");

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "removed",
            installation = new { id = 7777L },
            repositories_removed = new[]
            {
                new { id = 22L, name = "gone", full_name = "octo/gone", @private = false }
            },
        });

        var response = await PostWebhookAsync("installation_repositories", Guid.NewGuid().ToString(), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var remaining = await db.GithubRepositories
            .Where(r => r.GithubInstallationId == inst.Id)
            .Select(r => r.GithubRepoId)
            .ToListAsync();
        remaining.Should().BeEquivalentTo(new[] { 11L });
    }

    // -----------------------------------------------------------------------
    // installation lifecycle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task installation_deleted_removes_installation_and_repos()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 8000L, login: "octo");
        await SeedRepositoryAsync(inst, repoId: 1L, fullName: "octo/a");
        await SeedRepositoryAsync(inst, repoId: 2L, fullName: "octo/b");

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "deleted",
            installation = new { id = 8000L },
        });

        var response = await PostWebhookAsync("installation", Guid.NewGuid().ToString(), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.GithubInstallations.AnyAsync(i => i.Id == inst.Id)).Should().BeFalse();
        (await db.GithubRepositories.AnyAsync(r => r.GithubInstallationId == inst.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task installation_suspend_sets_suspended_true()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 9100L, login: "octo");
        inst.Suspended.Should().BeFalse();

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "suspend",
            installation = new { id = 9100L },
        });

        var response = await PostWebhookAsync("installation", Guid.NewGuid().ToString(), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var updated = await db.GithubInstallations.SingleAsync(i => i.Id == inst.Id);
        updated.Suspended.Should().BeTrue();
    }

    [Fact]
    public async Task installation_unsuspend_sets_suspended_false()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var inst = await SeedInstallationAsync(workspaceId, installationId: 9200L, login: "octo", suspended: true);

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            action = "unsuspend",
            installation = new { id = 9200L },
        });

        var response = await PostWebhookAsync("installation", Guid.NewGuid().ToString(), body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var updated = await db.GithubInstallations.SingleAsync(i => i.Id == inst.Id);
        updated.Suspended.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostWebhookAsync(
        string eventName,
        string deliveryId,
        byte[] body,
        string? signatureOverride = null)
    {
        await EnsureWebhookSecretSeededAsync();

        var signature = signatureOverride ?? Sign(body, WebhookSecret);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("X-GitHub-Event", eventName);
        content.Headers.Add("X-GitHub-Delivery", deliveryId);
        content.Headers.Add("X-Hub-Signature-256", signature);

        return await Client.PostAsync("/api/github/webhooks", content);
    }

    private static string Sign(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private async Task<Guid> SeedWorkspaceAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = new Source.Features.Workspaces.Models.Workspace
        {
            Id = Guid.NewGuid(),
            Slug = $"ws-{Guid.NewGuid():N}",
            Name = "Test workspace",
            OwnerId = Guid.NewGuid().ToString(),
        };
        db.Workspaces.Add(ws);
        await db.SaveChangesAsync();
        return ws.Id;
    }

    private async Task<GithubInstallation> SeedInstallationAsync(
        Guid workspaceId,
        long installationId,
        string login,
        bool suspended = false)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new GithubInstallation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            InstallationId = installationId,
            AccountLogin = login,
            AccountType = "Organization",
            Suspended = suspended,
        };
        db.GithubInstallations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task SeedRepositoryAsync(GithubInstallation inst, long repoId, string fullName)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var parts = fullName.Split('/', 2);
        db.GithubRepositories.Add(new GithubRepository
        {
            Id = Guid.NewGuid(),
            GithubInstallationId = inst.Id,
            GithubRepoId = repoId,
            Owner = parts[0],
            Name = parts.Length > 1 ? parts[1] : fullName,
            FullName = fullName,
            Private = false,
        });
        await db.SaveChangesAsync();
    }
}
