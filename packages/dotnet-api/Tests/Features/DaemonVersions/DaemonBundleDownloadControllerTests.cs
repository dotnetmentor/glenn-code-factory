using System.Net;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.DaemonVersions;
using Source.Features.DaemonVersions.Models;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;

namespace Api.Tests.Features.DaemonVersions;

[Collection(HangfireTestCollection.Name)]
public class DaemonBundleDownloadControllerTests : IntegrationTestBase
{
    private const string RegisteredFile = "20260601_085142_daemon-2026.6.1-085142.tar.gz";
    private static readonly byte[] BundleBytes = Encoding.UTF8.GetBytes("fake-daemon-bundle");

    public DaemonBundleDownloadControllerTests()
    {
        WithServiceFactory(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(new FakeFileStorageService());
        });
    }

    [Fact]
    public async Task Download_RegisteredBundle_Returns200WithoutAuth()
    {
        await SeedRegisteredBundleAsync();

        var response = await Client.GetAsync($"/api/files/daemon-bundles/{RegisteredFile}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/gzip");
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(BundleBytes);
    }

    [Fact]
    public async Task Download_UnregisteredBundle_Returns404()
    {
        await SeedRegisteredBundleAsync();

        var response = await Client.GetAsync(
            "/api/files/daemon-bundles/20990101_000000_daemon-9.9.9-999999.tar.gz");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../uploads/secret.tar.gz")]
    [InlineData("..%2f..%2fetc%2fpasswd.tar.gz")]
    [InlineData("foo/bar.tar.gz")]
    public async Task Download_TraversalOrInvalidPath_Returns404(string path)
    {
        await SeedRegisteredBundleAsync();

        var response = await Client.GetAsync($"/api/files/daemon-bundles/{path}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    private async Task SeedRegisteredBundleAsync()
    {
        _ = Client;
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.DaemonVersions.Add(new DaemonVersion
        {
            Version = "2026.6.1-085142",
            Channel = "stable",
            BundleStorageKey = DaemonBundleStorage.BuildStorageKey(RegisteredFile),
            BundleSha256 = new string('a', 64),
            BundleSizeBytes = BundleBytes.Length,
            ReleasedAt = DateTime.UtcNow,
            IsActive = true,
        });

        await db.SaveChangesAsync();
    }

    private sealed class FakeFileStorageService : IFileStorageService
    {
        public Task<string> SaveFileAsync(
            Stream fileStream,
            string fileName,
            string? folder = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(folder != null ? $"{folder}/{fileName}" : fileName);

        public Task<Stream> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (filePath == DaemonBundleStorage.BuildStorageKey(RegisteredFile))
            {
                return Task.FromResult<Stream>(new MemoryStream(BundleBytes, writable: false));
            }

            throw new FileNotFoundException($"File not found: {filePath}");
        }

        public Task<bool> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(filePath == DaemonBundleStorage.BuildStorageKey(RegisteredFile));

        public Task<string> GetFileUrlAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult($"/api/files/{filePath.Replace('\\', '/')}");

        public Task<string> GetPresignedPutUrlAsync(
            string key,
            string? contentType,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"/api/files/local-put/{key}");

        public Task<string> GetPresignedGetUrlAsync(
            string key,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"/api/files/{key.Replace('\\', '/')}");
    }
}
