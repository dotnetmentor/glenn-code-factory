using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement.Models;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Smoke tests for the <see cref="FlyOperation"/> EF model. We stay on the
/// shared in-memory provider here — these are about confirming the DbSet is
/// wired up and round-trips every field, plus that the dominant query
/// patterns (idempotency lookup + latest-per-runtime) actually return what
/// callers will expect. Postgres-specific index ordering is verified by the
/// migration itself, not here.
/// </summary>
public class FlyOperationPersistenceTests : HandlerTestBase
{
    [Fact]
    public async Task Insert_and_read_back_preserves_all_fields()
    {
        var id = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();
        var op = new FlyOperation
        {
            Id = id,
            RuntimeId = runtimeId,
            Operation = "CreateMachine",
            RequestKey = $"machineCreate:{runtimeId}",
            RequestPayload = "{\"region\":\"arn\"}",
            ResponsePayload = "{\"id\":\"abc123\"}",
            HttpStatusCode = 200,
            Status = FlyOperationStatus.Succeeded,
            ErrorCode = null,
            LatencyMs = 412,
        };
        Context.FlyOperations.Add(op);
        await Context.SaveChangesAsync();

        // Detach so the next read materialises from the store, not the change tracker.
        Context.ChangeTracker.Clear();

        var loaded = await Context.FlyOperations.SingleAsync(o => o.Id == id);

        loaded.RuntimeId.Should().Be(runtimeId);
        loaded.Operation.Should().Be("CreateMachine");
        loaded.RequestKey.Should().Be($"machineCreate:{runtimeId}");
        loaded.RequestPayload.Should().Be("{\"region\":\"arn\"}");
        loaded.ResponsePayload.Should().Be("{\"id\":\"abc123\"}");
        loaded.HttpStatusCode.Should().Be(200);
        loaded.Status.Should().Be(FlyOperationStatus.Succeeded);
        loaded.ErrorCode.Should().BeNull();
        loaded.LatencyMs.Should().Be(412);
        loaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync via IAuditable");
        loaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync via IAuditable");
    }

    [Fact]
    public async Task Lookup_by_request_key_returns_matching_row()
    {
        var runtimeId = Guid.NewGuid();
        var requestKey = $"volumeDestroy:{runtimeId}";

        Context.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            Operation = "DestroyVolume",
            RequestKey = requestKey,
            RequestPayload = "{}",
            Status = FlyOperationStatus.Succeeded,
            HttpStatusCode = 200,
            LatencyMs = 50,
        });
        // Noise — a different op that must not match.
        Context.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            Operation = "ListMachines",
            RequestKey = null,
            RequestPayload = "{}",
            Status = FlyOperationStatus.Succeeded,
            HttpStatusCode = 200,
            LatencyMs = 12,
        });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var found = await Context.FlyOperations
            .Where(o => o.RequestKey == requestKey)
            .ToListAsync();

        found.Should().HaveCount(1);
        found.Single().Operation.Should().Be("DestroyVolume");
    }

    [Fact]
    public async Task Latest_ops_for_runtime_come_back_in_descending_created_order()
    {
        var runtimeId = Guid.NewGuid();
        var otherRuntimeId = Guid.NewGuid();

        // Insert in deliberately scrambled order. SaveChanges sets CreatedAt to
        // DateTime.UtcNow per call, so saving each one separately gives us a real
        // monotonic timestamp ordering to test against.
        for (var i = 0; i < 12; i++)
        {
            Context.FlyOperations.Add(new FlyOperation
            {
                Id = Guid.NewGuid(),
                RuntimeId = runtimeId,
                Operation = $"Op{i}",
                RequestPayload = "{}",
                Status = FlyOperationStatus.Succeeded,
                HttpStatusCode = 200,
                LatencyMs = i,
            });
            await Context.SaveChangesAsync();
        }

        // Noise — different runtime, must not appear.
        Context.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = otherRuntimeId,
            Operation = "OtherRuntimeOp",
            RequestPayload = "{}",
            Status = FlyOperationStatus.Succeeded,
            HttpStatusCode = 200,
            LatencyMs = 1,
        });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var latest = await Context.FlyOperations
            .Where(o => o.RuntimeId == runtimeId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(10)
            .ToListAsync();

        latest.Should().HaveCount(10);
        latest.Should().OnlyContain(o => o.RuntimeId == runtimeId);
        // Most recent insert was Op11 — it must be first.
        latest.First().Operation.Should().Be("Op11");
        // Strictly non-increasing CreatedAt — the index promises this ordering.
        latest.Zip(latest.Skip(1))
            .Should()
            .OnlyContain(pair => pair.First.CreatedAt >= pair.Second.CreatedAt);
    }

    [Fact]
    public async Task Status_round_trips_through_string_conversion()
    {
        // Belt-and-braces: the column is mapped as string. If someone removes
        // .HasConversion<string>() we want to know via this round-trip.
        var pending = new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = "CreateMachine",
            RequestPayload = "{}",
            Status = FlyOperationStatus.Pending,
        };
        var failed = new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = "CreateMachine",
            RequestPayload = "{}",
            Status = FlyOperationStatus.Failed,
            ErrorCode = "rate_limited",
            HttpStatusCode = 429,
        };
        Context.FlyOperations.AddRange(pending, failed);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var loadedPending = await Context.FlyOperations.SingleAsync(o => o.Id == pending.Id);
        var loadedFailed = await Context.FlyOperations.SingleAsync(o => o.Id == failed.Id);

        loadedPending.Status.Should().Be(FlyOperationStatus.Pending);
        loadedFailed.Status.Should().Be(FlyOperationStatus.Failed);
        loadedFailed.ErrorCode.Should().Be("rate_limited");
    }
}
