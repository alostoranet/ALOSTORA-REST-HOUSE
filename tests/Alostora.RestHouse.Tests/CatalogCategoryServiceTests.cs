using Alostora.RestHouse;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class CatalogCategoryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly CatalogCategoryService _categories;
    private readonly string _databasePath;

    public CatalogCategoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-categories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "categories.db");
        _categories = new CatalogCategoryService(_databasePath);
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
    public void DeletingMainCategoryRemovesItsTreeAndUnpublishesLinkedContentWithoutDeletingVideoFiles()
    {
        var metadata = new VideoMetadataService(_databasePath);
        var series = new SeriesCatalogService(_databasePath);
        _categories.Save(new SaveCatalogCategoryRequest(null, null, "المكتبة", "Group", null, true));
        var main = Assert.Single(_categories.List());
        _categories.Save(new SaveCatalogCategoryRequest(null, main.CategoryId, "الأفلام", "Movie", null, true));
        var movieCategory = _categories.List().Single(x => x.Title == "الأفلام");
        _categories.Save(new SaveCatalogCategoryRequest(null, main.CategoryId, "المسلسلات", "Series", null, true));
        var seriesCategory = _categories.List().Single(x => x.Title == "المسلسلات");

        var mediaRoot = Path.Combine(_root, "media");
        Directory.CreateDirectory(mediaRoot);
        var videoPath = Path.Combine(mediaRoot, "film.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 1, 2, 3, 4 });
        using var library = new LibraryService(mediaRoot, _databasePath);
        var video = Assert.Single(library.List());

        metadata.Save(new SaveVideoMetadataRequest(
            video.Id, "فيلم اختباري", "", 2025, "", "Movie", null,
            IsPublished: true, CategoryId: movieCategory.CategoryId));
        series.SaveSeries(new SaveSeriesRequest(
            "series-test", "مسلسل اختباري", "", 2025, "", null,
            IsPublished: true, CategoryId: seriesCategory.CategoryId));

        _categories.Delete(main.CategoryId);

        Assert.Empty(_categories.List());
        var savedVideo = metadata.Get(video.Id);
        Assert.NotNull(savedVideo);
        Assert.Null(savedVideo.CategoryId);
        Assert.False(savedVideo.IsPublished);
        var savedSeries = series.GetSeries("series-test");
        Assert.NotNull(savedSeries);
        Assert.Null(savedSeries.CategoryId);
        Assert.False(savedSeries.IsPublished);
        Assert.True(File.Exists(videoPath));
        Assert.NotNull(library.Resolve(video.Id));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
