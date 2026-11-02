using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class RestHouseProfileServiceTests : IDisposable
{
    private readonly string _root;
    private readonly RestHouseProfileService _profile;

    public RestHouseProfileServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _profile = new RestHouseProfileService(_root);
    }

    [Fact]
    public void MissingProfileUsesSafeDefaults()
    {
        var value = _profile.Load();
        Assert.Equal("استراحة الأسطورة", value.RestHouseName);
        Assert.Equal("شبكة الأسطورة نت", value.NetworkName);
        Assert.Null(value.LogoPath);
    }

    [Fact]
    public void SaveAndReloadPreservesNamesAndLogoPath()
    {
        var expected = new RestHouseProfile("استراحة العائلة", "شبكة خاصة", "/posters/brand-logo.png");
        _profile.Save(expected);
        Assert.Equal(expected, _profile.Load());
    }

    [Fact]
    public void InvalidNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            _profile.Save(new RestHouseProfile(" ", "شبكة", null)));
        Assert.Throws<ArgumentException>(() =>
            _profile.Save(new RestHouseProfile("استراحة", new string('ن', 101), null)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
