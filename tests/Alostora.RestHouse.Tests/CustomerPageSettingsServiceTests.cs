using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class CustomerPageSettingsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly CustomerPageSettingsService _service;

    public CustomerPageSettingsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-customer-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new CustomerPageSettingsService(_root);
    }

    [Fact]
    public void MissingSettingsEnableAllCustomerSectionsByDefault()
    {
        var value = _service.Load();
        Assert.True(value.ShowCatalog);
        Assert.True(value.ShowContinueWatching);
        Assert.True(value.ShowAudio);
        Assert.True(value.ShowBooks);
        Assert.True(value.ShowApps);
        Assert.True(value.ShowContact);
        Assert.True(value.ShowWelcome);
        Assert.True(value.ShowAnnouncement);
        Assert.True(value.ShowDownloads);
        Assert.True(value.ShowImageAds);
    }

    [Fact]
    public void LegacySettingsWithoutDownloadVisibilityKeepDownloadsEnabled()
    {
        File.WriteAllText(Path.Combine(_root, "customer-page-settings.json"),
            "{\"ShowCatalog\":false,\"ShowAnnouncement\":true}");
        Assert.True(_service.Load().ShowDownloads);
        Assert.True(_service.Load().ShowImageAds);
    }

    [Fact]
    public void SavesAndReloadsExternalLiveBroadcastUrl()
    {
        const string url = "https://example.com/live/channel";
        var saved = _service.Save(new CustomerPageSettings(LiveBroadcastUrl: url));
        Assert.Equal(url, saved.LiveBroadcastUrl);
        Assert.Equal(url, _service.Load().LiveBroadcastUrl);
    }

    [Fact]
    public void SavesAndReloadsIndependentSectionVisibility()
    {
        var saved = _service.Save(new CustomerPageSettings(
            ShowCatalog: true, ShowContinueWatching: false, ShowAudio: true,
            ShowBooks: false, ShowApps: false, ShowContact: true,
            ShowWelcome: false, ShowAnnouncement: true, ShowDownloads: false, ShowImageAds: false));
        var loaded = _service.Load();
        Assert.Equal(saved, loaded);
        Assert.False(loaded.ShowContinueWatching);
        Assert.False(loaded.ShowBooks);
        Assert.False(loaded.ShowApps);
        Assert.False(loaded.ShowWelcome);
        Assert.False(loaded.ShowDownloads);
        Assert.False(loaded.ShowImageAds);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
