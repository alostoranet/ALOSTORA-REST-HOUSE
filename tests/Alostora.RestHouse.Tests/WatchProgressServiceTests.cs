using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class WatchProgressServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WatchProgressService _service;

    public WatchProgressServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new WatchProgressService(Path.Combine(_root, "watch.db"));
    }

    [Fact]
    public void PlaybackTotalsAndPerVideoCountsIncludeTheFullHistory()
    {
        const string videoA = "0123456789abcdef01234567";
        const string videoB = "abcdef0123456789abcdef01";
        _service.RecordPlayback(new WatchPlaybackRequest("viewer-1", videoA, "play-1"));
        _service.RecordPlayback(new WatchPlaybackRequest("viewer-1", videoA, "play-2"));
        _service.RecordPlayback(new WatchPlaybackRequest("viewer-2", videoB, "play-3"));

        var counts = _service.GetViewCounts();
        Assert.Equal(3, _service.GetTotalPlaybackCount());
        Assert.Equal(2, counts[videoA]);
        Assert.Equal(1, counts[videoB]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
