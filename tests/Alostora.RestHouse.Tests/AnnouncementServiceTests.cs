using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class AnnouncementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-announcement-" + Guid.NewGuid().ToString("N"));
    private AnnouncementService Create() => new(_root);

    [Fact]
    public void MissingAnnouncementIsInactiveByDefault()
    {
        var value = Create().Load();
        Assert.False(value.IsActive);
        Assert.Empty(value.Message);
    }

    [Fact]
    public void SavesAndReloadsAnnouncementLocally()
    {
        var service = Create();
        var saved = service.Save(new RestHouseAnnouncement("عرض", "رسالة للعملاء", true, "ad", DateTime.UtcNow, 20));
        var loaded = service.Load();
        Assert.Equal(saved.Title, loaded.Title);
        Assert.Equal(saved.Message, loaded.Message);
        Assert.True(loaded.IsActive);
        Assert.Equal("ad", loaded.Kind);
        Assert.Equal(20, loaded.SpeedSeconds);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(18, 18)]
    [InlineData(90, 60)]
    public void ClampsScrollDuration(int supplied, int expected)
    {
        var saved = Create().Save(new RestHouseAnnouncement("", "رسالة", true, "notice", DateTime.UtcNow, supplied));
        Assert.Equal(expected, saved.SpeedSeconds);
    }

    [Fact]
    public void RejectsActiveEmptyOrOversizedAnnouncement()
    {
        var service = Create();
        Assert.Throws<ArgumentException>(() => service.Save(new RestHouseAnnouncement("", "", true, "ad", DateTime.UtcNow)));
        Assert.Throws<ArgumentException>(() => service.Save(new RestHouseAnnouncement("", new string('ن', 1001), true, "ad", DateTime.UtcNow)));
        Assert.Throws<ArgumentException>(() => service.Save(new RestHouseAnnouncement(new string('ع', 121), "رسالة", false, "notice", DateTime.UtcNow)));
    }

    [Fact]
    public void InvalidKindFallsBackToNotice()
    {
        var saved = Create().Save(new RestHouseAnnouncement("", "رسالة", true, "unexpected", DateTime.UtcNow));
        Assert.Equal("notice", saved.Kind);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
