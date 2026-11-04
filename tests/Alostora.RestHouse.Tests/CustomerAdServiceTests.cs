using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class CustomerAdServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alostora-customer-ads-" + Guid.NewGuid().ToString("N"));
    private CustomerAdService Create()
    {
        var posters = Path.Combine(_root, "posters");
        Directory.CreateDirectory(posters);
        return new CustomerAdService(_root, posters);
    }

    private static MemoryStream Png() => new(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 });

    [Fact]
    public void AddsListsAndServesOnlyActiveAds()
    {
        var service = Create();
        var first = service.Add(Png(), "ad.png", "عرض الاستراحة", "https://example.com", 10);
        var second = service.Add(Png(), "second.png", "إعلان ثانٍ", "", 15);
        Assert.Equal(2, service.List().Count);
        Assert.Equal(2, service.List(activeOnly: true).Count);
        Assert.True(service.SetVisibility(first.Id, false));
        Assert.Single(service.List(activeOnly: true));
        Assert.False(service.TryGetImage(first.Id, out _, out _));
        Assert.True(service.TryGetImage(second.Id, out var path, out var contentType));
        Assert.True(File.Exists(path));
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public void UpdatesAndMovesAdsWithoutLosingTheirImages()
    {
        var service = Create();
        var first = service.Add(Png(), "a.png", "الأول", "", 10);
        var second = service.Add(Png(), "b.png", "الثاني", "", 10);
        service.Update(first.Id, new CustomerAdUpdateRequest("الأول المعدل", "https://example.com", 20, true));
        Assert.True(service.Move(second.Id, "up"));
        Assert.Equal(second.Id, service.List()[0].Id);
        Assert.Equal("الأول المعدل", service.List().Single(x => x.Id == first.Id).Title);
    }

    [Fact]
    public void RejectsUnsupportedOrMismatchedImagesAndUnsafeLinks()
    {
        var service = Create();
        Assert.Throws<ArgumentException>(() => service.Add(new MemoryStream(new byte[] { 1, 2, 3 }), "fake.png", "", "", 10));
        Assert.Throws<ArgumentException>(() => service.Add(Png(), "file.svg", "", "", 10));
        Assert.Throws<ArgumentException>(() => service.Add(Png(), "file.png", "", "javascript:alert(1)", 10));
        Assert.Throws<ArgumentException>(() => service.Add(Png(), "file.png", "", "", 2));
    }

    [Fact]
    public void RemovesOnlyTheManagedAdImageAndMetadata()
    {
        var service = Create();
        var ad = service.Add(Png(), "ad.png", "إعلان", "", 10);
        Assert.True(service.TryGetImage(ad.Id, out var path, out _));
        Assert.True(service.Remove(ad.Id));
        Assert.False(File.Exists(path));
        Assert.Empty(service.List());
        Assert.False(service.Remove(ad.Id));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
