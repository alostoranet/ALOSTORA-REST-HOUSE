using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class AnnouncementServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AnnouncementService _service;

    public AnnouncementServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-announcement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new AnnouncementService(_root);
    }

    [Fact]
    public void MissingAnnouncementIsInactiveByDefault()
    {
        var value = _service.Load();
        Assert.False(value.IsActive);
        Assert.Equal("", value.Message);
    }

    [Fact]
    public void SavesAndReloadsAnnouncementLocally()
    {
        var saved = _service.Save(new RestHouseAnnouncement("تنبيه", "سيتم تحديث المكتبة مساءً", true, "notice", DateTime.UtcNow));
        var loaded = _service.Load();
        Assert.Equal(saved.Title, loaded.Title);
        Assert.Equal(saved.Message, loaded.Message);
        Assert.True(loaded.IsActive);
        Assert.Equal("notice", loaded.Kind);
        Assert.Equal(18, loaded.SpeedSeconds);
    }

    [Fact]
    public void ClampsAnnouncementScrollSpeedToSupportedRange()
    {
        var saved = _service.Save(new RestHouseAnnouncement("تنبيه", "اختبار السرعة", true, "notice", DateTime.UtcNow, SpeedSeconds: 2));
        Assert.Equal(5, saved.SpeedSeconds);
        Assert.Equal(5, _service.Load().SpeedSeconds);
    }

    [Fact]
    public void RejectsOversizedAnnouncement()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.Save(new RestHouseAnnouncement("", new string('ن', 1001), true, "ad", DateTime.UtcNow)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
