using Source.Features.DaemonVersions;

namespace Api.Tests.Features.DaemonVersions;

public class DaemonBundleStorageTests
{
    [Theory]
    [InlineData("20260601_085142_daemon-2026.6.1-085142.tar.gz")]
    [InlineData("20260101_000000_daemon-1.2.3-120000.tar.gz")]
    public void IsSafeFileName_accepts_published_bundle_names(string fileName)
    {
        DaemonBundleStorage.IsSafeFileName(fileName).Should().BeTrue();
        DaemonBundleStorage.IsAllowedStorageKey(DaemonBundleStorage.BuildStorageKey(fileName)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../secret.tar.gz")]
    [InlineData("..")]
    [InlineData("daemon-bundles/evil.tar.gz")]
    [InlineData("foo/bar.tar.gz")]
    [InlineData("foo\\bar.tar.gz")]
    [InlineData("%2e%2e%2fetc%2fpasswd.tar.gz")]
    [InlineData("bundle.tar")]
    [InlineData("bundle.zip")]
    [InlineData("bundle.tar.gz.exe")]
    public void IsSafeFileName_rejects_unsafe_names(string? fileName)
    {
        DaemonBundleStorage.IsSafeFileName(fileName).Should().BeFalse();
    }

    [Theory]
    [InlineData("uploads/leak.tar.gz")]
    [InlineData("daemon-bundles/nested/extra.tar.gz")]
    [InlineData("daemon-bundles/../uploads/leak.tar.gz")]
    public void IsAllowedStorageKey_rejects_keys_outside_bundle_folder(string storageKey)
    {
        DaemonBundleStorage.IsAllowedStorageKey(storageKey).Should().BeFalse();
    }
}
