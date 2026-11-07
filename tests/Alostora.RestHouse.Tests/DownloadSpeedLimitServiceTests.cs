using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class DownloadSpeedLimitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-download-speed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsToUnlimitedAndPersistsConfiguredLimit()
    {
        var service = new DownloadSpeedLimitService(_root);
        Assert.Equal(0, service.GetSettings().LimitMbps);

        service.Save(new DownloadSpeedLimitSettings(8));
        Assert.Equal(8, new DownloadSpeedLimitService(_root).GetSettings().LimitMbps);
    }

    [Fact]
    public void RejectsNegativeAndExcessiveLimits()
    {
        var service = new DownloadSpeedLimitService(_root);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Save(new DownloadSpeedLimitSettings(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Save(new DownloadSpeedLimitSettings(1001)));
    }

    [Fact]
    public async Task UnlimitedDownloadsDoNotWait()
    {
        var service = new DownloadSpeedLimitService(_root);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await service.WaitAsync(1024 * 1024, CancellationToken.None);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(250));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
