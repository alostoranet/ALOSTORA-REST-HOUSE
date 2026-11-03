using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class CatalogCategoryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogCategoryService _categories;

    public CatalogCategoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-categories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _categories = new CatalogCategoryService(Path.Combine(_root, "categories.db"));
    }

    [Fact]
    public void PublishedMainAndChildCategoriesAppearWithoutPosterImages()
    {
        _categories.Save(new SaveCatalogCategoryRequest(null, null, "إدارة الاستراحة", "Group", null, true));
        var main = Assert.Single(_categories.List());
        _categories.Save(new SaveCatalogCategoryRequest(null, main.CategoryId, "الأفلام", "Movie", null, true));

        var visible = _categories.List(publishedOnly: true);

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, x => x.Title == "إدارة الاستراحة" && x.ParentId is null);
        Assert.Contains(visible, x => x.Title == "الأفلام" && x.ParentId == main.CategoryId);
        Assert.All(visible, x => Assert.Null(x.PosterPath));
    }

    [Fact]
    public void UnpublishedCategoriesDoNotAppearInCustomerCatalog()
    {
        _categories.Save(new SaveCatalogCategoryRequest(null, null, "قسم مسودة", "Group", null, false));
        var draft = Assert.Single(_categories.List());

        Assert.Empty(_categories.List(publishedOnly: true));
        Assert.False(draft.IsPublished);
    }

    [Fact]
    public void PublishedChildOfUnpublishedParentIsNotExposed()
    {
        _categories.Save(new SaveCatalogCategoryRequest(null, null, "قسم مخفي", "Group", null, false));
        var main = Assert.Single(_categories.List());
        _categories.Save(new SaveCatalogCategoryRequest(null, main.CategoryId, "أفلام مخفية", "Movie", null, true));

        Assert.Empty(_categories.List(publishedOnly: true));
    }


    [Fact]
    public void DeletingMainCategoryRemovesCategoryTreeAndUnpublishesLinkedContentWithoutDeletingMedia()
    {
        const string videoId = "0123456789abcdef01234567";
        var mainId = "main-" + Guid.NewGuid().ToString("N");
        var childId = "child-" + Guid.NewGuid().ToString("N");
        var videoPath = Path.Combine(_root, "original-video.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 1, 2, 3, 4 });

        var series = new SeriesCatalogService(Path.Combine(_root, "categories.db"));
        var metadata = new VideoMetadataService(Path.Combine(_root, "categories.db"));
        _categories.Save(new SaveCatalogCategoryRequest(mainId, null, "الرئيسية", "Group", null, true));
        _categories.Save(new SaveCatalogCategoryRequest(childId, mainId, "المسلسلات", "Series", null, true));
        series.SaveSeries(new SaveSeriesRequest("series-delete-test", "مسلسل مرتبط", "", 2026, "", null,
            CategoryId: childId, IsPublished: true));
        metadata.Save(new SaveVideoMetadataRequest(videoId, "حلقة مرتبطة", "", null, "", "Series", null,
            CategoryId: childId, IsPublished: true));

        _categories.Delete(mainId);

        Assert.Empty(_categories.List());
        var savedSeries = Assert.Single(series.ListSeries());
        Assert.Null(savedSeries.CategoryId);
        Assert.False(savedSeries.IsPublished);
        var savedVideo = metadata.Get(videoId);
        Assert.NotNull(savedVideo);
        Assert.Null(savedVideo.CategoryId);
        Assert.False(savedVideo.IsPublished);
        Assert.True(File.Exists(videoPath));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
