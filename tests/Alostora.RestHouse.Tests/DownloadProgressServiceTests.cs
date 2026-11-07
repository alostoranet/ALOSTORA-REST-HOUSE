using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class DownloadProgressServiceTests
{
    [Fact]
    public void TracksProgressAndCompletesDownload()
    {
        var service = new DownloadProgressService();
        service.Begin("client-a", "download-a", "item-a", "book.pdf", 1000, 0);
        service.Report("client-a", "download-a", 420);

        var progress = Assert.Single(service.List("client-a"));
        Assert.Equal(420, progress.BytesTransferred);
        Assert.Equal(42, progress.Percent);
        Assert.Equal("جارٍ التنزيل", progress.Status);

        service.Complete("client-a", "download-a");
        progress = Assert.Single(service.List("client-a"));
        Assert.Equal(100, progress.Percent);
        Assert.Equal("اكتمل", progress.Status);
    }

    [Fact]
    public void ResumeStartsFromRequestedRangeAndKeepsClientHistoriesSeparate()
    {
        var service = new DownloadProgressService();
        service.Begin("client-a", "download-a", "item-a", "app.apk", 1000, 0);
        service.Report("client-a", "download-a", 350);
        service.Interrupt("client-a", "download-a");

        service.Begin("client-a", "download-a", "item-a", "app.apk", 1000, 350);
        Assert.Equal(350, Assert.Single(service.List("client-a")).BytesTransferred);
        Assert.Empty(service.List("client-b"));
    }

    [Fact]
    public void RemovingActiveHistoryDoesNotRecreateAnEmptyProgressRow()
    {
        var service = new DownloadProgressService();
        service.Begin("client-a", "download-a", "item-a", "book.pdf", 100, 0);

        Assert.True(service.Remove("client-a", "download-a"));
        service.Report("client-a", "download-a", 50);

        Assert.Empty(service.List("client-a"));
    }

    [Fact]
    public void KeepsDownloadHistoryBoundedPerClient()
    {
        var service = new DownloadProgressService();
        for (var i = 0; i < 40; i++)
            service.Begin("client-a", "download-" + i, "item-" + i, "file-" + i, 100, 0);

        Assert.Equal(30, service.List("client-a").Count);
        Assert.Empty(service.List("client-b"));
    }

    [Fact]
    public void CanRemoveOnlyTheDownloadHistoryEntry()
    {
        var service = new DownloadProgressService();
        service.Begin("client-a", "download-a", "item-a", "book.pdf", 100, 0);

        Assert.True(service.Remove("client-a", "download-a"));
        Assert.Empty(service.List("client-a"));
        Assert.False(service.Remove("client-a", "download-a"));
    }
}
