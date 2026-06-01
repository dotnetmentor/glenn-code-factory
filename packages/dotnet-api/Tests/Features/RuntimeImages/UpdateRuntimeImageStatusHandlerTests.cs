using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.RuntimeImages.Commands.UpdateRuntimeImageStatus;
using Source.Features.RuntimeImages.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeImages;

/// <summary>
/// Unit tests for <see cref="UpdateRuntimeImageStatusHandler"/>. The behaviour we care
/// about is the <em>single-Active invariant</em>: when a row is promoted to
/// <see cref="RuntimeImageStatus.Active"/> every other Active row must be demoted to
/// <see cref="RuntimeImageStatus.Deprecated"/> in the same transaction so
/// <c>RuntimeProvisionerJob</c> (which reads "newest row with Status == Active") never
/// observes more than one Active row.
/// </summary>
public class UpdateRuntimeImageStatusHandlerTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDbContextFactory.Create();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private UpdateRuntimeImageStatusHandler BuildHandler()
        => new(_db, NullLogger<UpdateRuntimeImageStatusHandler>.Instance);

    private async Task<RuntimeImage> SeedImageAsync(RuntimeImageStatus status, string tag, DateTime? builtAt = null)
    {
        var image = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = tag,
            Digest = $"sha256:{tag}",
            Registry = "registry.fly.io/glenn-runtime-base",
            GitSha = "deadbee",
            BuiltAt = builtAt ?? DateTime.UtcNow,
            SizeMb = 100,
            Status = status,
        };
        _db.RuntimeImages.Add(image);
        await _db.SaveChangesAsync();
        return image;
    }

    [Fact]
    public async Task NotFound_returns_failure_with_not_found_sentinel()
    {
        var handler = BuildHandler();
        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(Guid.NewGuid(), RuntimeImageStatus.Deprecated),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(UpdateRuntimeImageStatusHandler.NotFoundError);
    }

    [Fact]
    public async Task NoOp_when_status_unchanged_returns_existing_row()
    {
        var existing = await SeedImageAsync(RuntimeImageStatus.Deprecated, "tag-deprecated");
        var handler = BuildHandler();

        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(existing.Id, RuntimeImageStatus.Deprecated),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(existing.Id);
        result.Value.Status.Should().Be(RuntimeImageStatus.Deprecated);
    }

    [Fact]
    public async Task Promote_to_Active_demotes_all_other_Active_rows_to_Deprecated()
    {
        // Two Active rows already in the table — the contract requires the handler to
        // demote both before it lands the new Active.
        var oldActive1 = await SeedImageAsync(RuntimeImageStatus.Active, "old-active-1", DateTime.UtcNow.AddHours(-2));
        var oldActive2 = await SeedImageAsync(RuntimeImageStatus.Active, "old-active-2", DateTime.UtcNow.AddHours(-1));
        var deprecated = await SeedImageAsync(RuntimeImageStatus.Deprecated, "already-deprecated", DateTime.UtcNow.AddHours(-3));
        var yanked = await SeedImageAsync(RuntimeImageStatus.Yanked, "already-yanked", DateTime.UtcNow.AddHours(-4));
        var target = await SeedImageAsync(RuntimeImageStatus.Deprecated, "to-promote", DateTime.UtcNow);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(target.Id, RuntimeImageStatus.Active),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RuntimeImageStatus.Active);

        // Re-read from the DB to assert persisted state.
        var rows = await _db.RuntimeImages.AsNoTracking().ToListAsync();

        rows.Single(i => i.Id == target.Id).Status.Should().Be(RuntimeImageStatus.Active);
        rows.Single(i => i.Id == oldActive1.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
        rows.Single(i => i.Id == oldActive2.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
        // Non-Active rows must be untouched.
        rows.Single(i => i.Id == deprecated.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
        rows.Single(i => i.Id == yanked.Id).Status.Should().Be(RuntimeImageStatus.Yanked);

        // Pin the invariant: exactly one Active row after the transition.
        rows.Count(i => i.Status == RuntimeImageStatus.Active).Should().Be(1);
    }

    [Fact]
    public async Task Promote_when_no_other_Active_rows_simply_activates_target()
    {
        var target = await SeedImageAsync(RuntimeImageStatus.Deprecated, "lonely");
        var handler = BuildHandler();

        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(target.Id, RuntimeImageStatus.Active),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var rows = await _db.RuntimeImages.AsNoTracking().ToListAsync();
        rows.Count(i => i.Status == RuntimeImageStatus.Active).Should().Be(1);
        rows.Single().Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task Demote_to_Deprecated_does_not_touch_other_rows()
    {
        var keepActive = await SeedImageAsync(RuntimeImageStatus.Active, "keep-active");
        var target = await SeedImageAsync(RuntimeImageStatus.Active, "to-demote");
        var handler = BuildHandler();

        // Demoting one of two Active rows should never auto-promote anything else, just
        // change the target.
        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(target.Id, RuntimeImageStatus.Deprecated),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var rows = await _db.RuntimeImages.AsNoTracking().ToListAsync();
        rows.Single(i => i.Id == target.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
        rows.Single(i => i.Id == keepActive.Id).Status.Should().Be(RuntimeImageStatus.Active);
    }

    [Fact]
    public async Task Yank_target_does_not_demote_others()
    {
        var otherActive = await SeedImageAsync(RuntimeImageStatus.Active, "other-active");
        var target = await SeedImageAsync(RuntimeImageStatus.Active, "to-yank");
        var handler = BuildHandler();

        var result = await handler.Handle(
            new UpdateRuntimeImageStatusCommand(target.Id, RuntimeImageStatus.Yanked),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var rows = await _db.RuntimeImages.AsNoTracking().ToListAsync();
        rows.Single(i => i.Id == target.Id).Status.Should().Be(RuntimeImageStatus.Yanked);
        rows.Single(i => i.Id == otherActive.Id).Status.Should().Be(RuntimeImageStatus.Active);
    }
}
