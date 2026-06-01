using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Events;
using Source.Features.FlyManagement.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// End-to-end tests for the public Fly webhook receiver
/// (<c>POST /api/webhooks/fly</c>). The HMAC verifier runs unmocked — tests sign their
/// own bodies with the same secret the host reads through <see cref="FlyOptions"/>.
///
/// <para>The published <see cref="FlyMachineStateChanged"/> notification is captured by
/// a test-scoped <see cref="INotificationHandler{TNotification}"/> registered on the
/// host so we can assert dispatch happened (or didn't, on the negative paths).</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class FlyWebhookControllerTests : IntegrationTestBase
{
    private const string WebhookSecret = "test-fly-webhook-secret-xyz";

    private readonly CapturingHandler _capturedEvents = new();

    public FlyWebhookControllerTests()
    {
        // Register a transient INotificationHandler<FlyMachineStateChanged> that points at our
        // shared CapturingHandler. MediatR resolves notification handlers per Publish call,
        // so we add the singleton sink and a transient adapter that hands events off to it.
        WithServiceFactory(services =>
        {
            services.AddSingleton(_capturedEvents);
            services.AddTransient<INotificationHandler<FlyMachineStateChanged>, CapturingHandlerAdapter>();
        });
    }

    private bool _settingsSeeded;

    private async Task EnsureSecretSeededAsync(string? secret = WebhookSecret)
    {
        if (_settingsSeeded) return;
        using var scope = CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
        // Setting an empty value is the "secret missing" path — we still call SetAsync so
        // SystemSettings has a row (matches what the production seeder does).
        await settings.SetAsync("Fly:WebhookSecret", secret ?? string.Empty, isSecret: true);
        _settingsSeeded = true;
    }

    // -----------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receive_ValidHmac_Returns200_WritesAuditRow_PublishesEvent()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = BuildPayload(eventId, eventType: "machine.state_changed",
            machineId: "m-123", previousState: "started", newState: "stopped");

        var response = await PostWebhookAsync(body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Audit row was written with the dedup-safe RequestKey shape.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var op = await db.FlyOperations.SingleAsync(o => o.RequestKey == $"webhook:{eventId}");
        op.Operation.Should().Be("Webhook:machine.state_changed");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
        op.RequestPayload.Should().Contain(eventId);

        // Domain event was published.
        _capturedEvents.Events.Should().HaveCount(1);
        var evt = _capturedEvents.Events[0];
        evt.FlyEventId.Should().Be(eventId);
        evt.MachineId.Should().Be("m-123");
        evt.NewState.Should().Be("stopped");
        evt.PreviousState.Should().Be("started");
    }

    // -----------------------------------------------------------------------
    // Signature failures
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receive_InvalidHmac_Returns401_NoAudit_NoEvent()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = BuildPayload(eventId, eventType: "machine.state_changed",
            machineId: "m-1", previousState: null, newState: "started");

        // Replace the signature with an all-zero hex string of correct length — syntactically
        // valid, semantically wrong.
        var response = await PostWebhookAsync(body, signatureOverride: new string('0', 64));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertNoAuditAndNoEventAsync(eventId);
    }

    [Fact]
    public async Task Receive_MissingSignatureHeader_Returns401()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = BuildPayload(eventId, eventType: "machine.state_changed",
            machineId: "m-1", previousState: null, newState: "started");

        await EnsureSecretSeededAsync();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // No Fly-Webhook-Signature header attached.

        var response = await Client.PostAsync("/api/webhooks/fly", content);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertNoAuditAndNoEventAsync(eventId);
    }

    [Fact]
    public async Task Receive_NoSecretConfigured_Returns500()
    {
        // Seed the WebhookSecret row as empty — IFlyOptionsAccessor will surface "" and the
        // controller hard-fails 500 rather than silently accepting.
        await EnsureSecretSeededAsync(secret: string.Empty);

        var eventId = Guid.NewGuid().ToString();
        var body = BuildPayload(eventId, eventType: "machine.state_changed",
            machineId: "m-1", previousState: null, newState: "started");

        // Sign with *some* secret so we'd otherwise pass — the missing config still wins.
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("Fly-Webhook-Signature", Sign(body, "any-non-empty"));

        var response = await Client.PostAsync("/api/webhooks/fly", content);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await AssertNoAuditAndNoEventAsync(eventId);
    }

    // -----------------------------------------------------------------------
    // Idempotency
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receive_DuplicateEventIdWithin5Min_Returns200_NoNewAudit_NoNewEvent()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = BuildPayload(eventId, eventType: "machine.state_changed",
            machineId: "m-7", previousState: "started", newState: "suspended");

        var first = await PostWebhookAsync(body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PostWebhookAsync(body);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // Audit is written exactly once.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.FlyOperations.Where(o => o.RequestKey == $"webhook:{eventId}").ToListAsync();
        rows.Should().HaveCount(1);

        // And the domain event was published exactly once.
        _capturedEvents.Events.Should().HaveCount(1);
    }

    // -----------------------------------------------------------------------
    // Payload validation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receive_InvalidJson_Returns400()
    {
        var body = Encoding.UTF8.GetBytes("this-is-not-json");
        var response = await PostWebhookAsync(body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // No event_id to key off, but assert the domain side stayed quiet.
        _capturedEvents.Events.Should().BeEmpty();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.FlyOperations.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Receive_EmptyEventId_Returns400()
    {
        // event_id is the dedup key; empty/missing must hard-reject so we never write an
        // un-deduppable audit row.
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            event_id = "",
            event_type = "machine.state_changed",
            machine_id = "m-1",
            new_state = "started",
        });

        var response = await PostWebhookAsync(body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        _capturedEvents.Events.Should().BeEmpty();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.FlyOperations.AnyAsync()).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private async Task<HttpResponseMessage> PostWebhookAsync(byte[] body, string? signatureOverride = null)
    {
        await EnsureSecretSeededAsync();
        var sig = signatureOverride ?? Sign(body, WebhookSecret);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("Fly-Webhook-Signature", sig);
        return await Client.PostAsync("/api/webhooks/fly", content);
    }

    private async Task AssertNoAuditAndNoEventAsync(string eventId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.FlyOperations.AnyAsync(o => o.RequestKey == $"webhook:{eventId}")).Should().BeFalse();
        _capturedEvents.Events.Should().BeEmpty();
    }

    private static byte[] BuildPayload(
        string eventId,
        string eventType,
        string? machineId,
        string? previousState,
        string? newState)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            event_id = eventId,
            event_type = eventType,
            machine_id = machineId,
            previous_state = previousState,
            new_state = newState,
            occurred_at = DateTime.UtcNow,
        });
    }

    private static string Sign(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    /// <summary>
    /// Singleton sink that buffers every <see cref="FlyMachineStateChanged"/> the test host
    /// publishes. The companion <see cref="CapturingHandlerAdapter"/> is the actual MediatR
    /// handler — the indirection lets us share state across MediatR's per-publish DI scope.
    /// </summary>
    private sealed class CapturingHandler
    {
        public List<FlyMachineStateChanged> Events { get; } = new();
    }

    private sealed class CapturingHandlerAdapter : INotificationHandler<FlyMachineStateChanged>
    {
        private readonly CapturingHandler _sink;
        public CapturingHandlerAdapter(CapturingHandler sink) => _sink = sink;

        public Task Handle(FlyMachineStateChanged notification, CancellationToken cancellationToken)
        {
            _sink.Events.Add(notification);
            return Task.CompletedTask;
        }
    }
}
