using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Features.SignalR.Services;
using Source.Features.SystemSettings.Services;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Xunit;

namespace Api.Tests.Features.SignalR;

public sealed class CursorApiKeyResolverTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly CursorApiKeyResolver _resolver;

    public CursorApiKeyResolverTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _encryption = new SecretEncryptionService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SecretEncryptionService>.Instance);
        _resolver = new CursorApiKeyResolver(_db, _encryption, NullLogger<CursorApiKeyResolver>.Instance);
    }

    [Fact]
    public async Task Resolve_uses_project_key_when_set()
    {
        var (workspaceId, projectId) = await SeedWorkspaceAndProjectAsync();

        var envelope = await EncryptProjectKeyAsync(projectId, "project-key");
        await SetProjectKeyAsync(projectId, envelope);

        var resolved = await _resolver.ResolveForProjectAsync(projectId, CancellationToken.None);

        resolved.Should().Be("project-key");
    }

    [Fact]
    public async Task Resolve_falls_back_to_workspace_key_when_project_unset()
    {
        var (workspaceId, projectId) = await SeedWorkspaceAndProjectAsync();

        var envelope = await EncryptWorkspaceKeyAsync(workspaceId, "workspace-key");
        await SetWorkspaceKeyAsync(workspaceId, envelope);

        var resolved = await _resolver.ResolveForProjectAsync(projectId, CancellationToken.None);

        resolved.Should().Be("workspace-key");
    }

    [Fact]
    public async Task GetStatus_reflects_project_and_workspace_presence()
    {
        var (workspaceId, projectId) = await SeedWorkspaceAndProjectAsync();

        await SetWorkspaceKeyAsync(
            workspaceId,
            await EncryptWorkspaceKeyAsync(workspaceId, "ws-key"));

        var status = await _resolver.GetStatusForProjectAsync(projectId, CancellationToken.None);

        status.HasWorkspaceCursorApiKey.Should().BeTrue();
        status.HasProjectCursorApiKey.Should().BeFalse();
        status.HasEffectiveCursorApiKey.Should().BeTrue();
        status.AllowProjectCursorApiKeyOverride.Should().BeTrue();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
    }

    private async Task<(Guid WorkspaceId, Guid ProjectId)> SeedWorkspaceAndProjectAsync()
    {
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = "acme",
            Name = "Acme",
            OwnerId = "owner-1",
        };
        var project = new Source.Features.Projects.Models.Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            OwnerUserId = "owner-1",
            Name = "app",
            GithubRepoOwner = "o",
            GithubRepoName = "r",
        };
        _db.Workspaces.Add(workspace);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return (workspace.Id, project.Id);
    }

    private async Task<string> EncryptProjectKeyAsync(Guid projectId, string plaintext)
    {
        var (ciphertext, nonce, dekVersion) =
            await _encryption.EncryptAsync(projectId, plaintext, CancellationToken.None);
        return ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
    }

    private async Task<string> EncryptWorkspaceKeyAsync(Guid workspaceId, string plaintext)
    {
        var (ciphertext, nonce, dekVersion) =
            await _encryption.EncryptForWorkspaceAsync(workspaceId, plaintext, CancellationToken.None);
        return ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
    }

    private async Task SetProjectKeyAsync(Guid projectId, string envelope)
    {
        var project = await _db.Projects.FirstAsync(p => p.Id == projectId);
        project.EncryptedCursorApiKey = envelope;
        await _db.SaveChangesAsync();
    }

    private async Task SetWorkspaceKeyAsync(Guid workspaceId, string envelope)
    {
        var workspace = await _db.Workspaces.FirstAsync(w => w.Id == workspaceId);
        workspace.EncryptedCursorApiKey = envelope;
        await _db.SaveChangesAsync();
    }

}
