using Alostora.RestHouse;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Alostora.RestHouse.Tests;

public sealed class PublicCatalogAccessTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly CatalogCategoryService _categories;
    private readonly VideoMetadataService _metadata;
    private readonly SeriesCatalogService _series;
    private readonly PublicCatalogAccessService _access;

    public PublicCatalogAccessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-public-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "test.db");

        _metadata = new VideoMetadataService(_databasePath);
        _series = new SeriesCatalogService(_databasePath);
        _categories = new CatalogCategoryService(_databasePath);
        _access = new PublicCatalogAccessService(_databasePath);
    }

    [Fact]
    public void PublishedMovieRequiresPublishedVideoAndPublishedCategoryTree()
    {
        CreateRootAndMovieCategory();

        const string videoId = "aaaaaaaaaaaaaaaaaaaaaaaa";
        SaveMovie(videoId, isPublished: true);

        Assert.True(_access.IsVideoPublished(videoId));
        Assert.Contains(videoId, _access.GetPublishedVideoIds());

        const string otherKindId = "cccccccccccccccccccccccc";
        _metadata.Save(new SaveVideoMetadataRequest(
            VideoId: otherKindId,
            Title: "محتوى آخر",
            Description: "",
            Year: null,
            Genre: "",
            Kind: "Other",
            PosterPath: null,
            IsPublished: true,
            CategoryId: "movies"));
        Assert.True(_access.IsVideoPublished(otherKindId));
        Assert.Contains(otherKindId, _access.GetPublishedVideoIds());

        SaveMovie(videoId, isPublished: false);
        Assert.False(_access.IsVideoPublished(videoId));
        Assert.DoesNotContain(videoId, _access.GetPublishedVideoIds());

        SaveMovie(videoId, isPublished: true);
        _categories.Save(new SaveCatalogCategoryRequest("movies", "root", "أفلام", "Movie", null, false));

        Assert.False(_access.IsVideoPublished(videoId));
        Assert.DoesNotContain(videoId, _access.GetPublishedVideoIds());
        Assert.DoesNotContain(_categories.List(publishedOnly: true), category => category.CategoryId == "movies");
    }

    [Fact]
    public void EpisodeIsPublicOnlyWhenItsSeriesAndCategoryTreeArePublished()
    {
        CreateRootAndSeriesCategory();

        const string episodeId = "bbbbbbbbbbbbbbbbbbbbbbbb";
        _series.SaveSeries(new SaveSeriesRequest(
            SeriesId: "series-1",
            Title: "مسلسل اختباري",
            Description: null,
            Year: null,
            Genre: null,
            PosterPath: null,
            IsPublished: true,
            CategoryId: "series-category"));

        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-1", 1, "الموسم الأول"));
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, episodeId, 1, "الحلقة الأولى"));

        Assert.True(_access.IsVideoPublished(episodeId));
        Assert.Contains(episodeId, _access.GetPublishedVideoIds());
        Assert.Contains("series-1", _access.GetPublishedSeriesIds());

        _series.SaveSeries(new SaveSeriesRequest(
            SeriesId: "series-1",
            Title: "مسلسل اختباري",
            Description: null,
            Year: null,
            Genre: null,
            PosterPath: null,
            IsPublished: false,
            CategoryId: "series-category"));

        Assert.False(_access.IsVideoPublished(episodeId));
        Assert.DoesNotContain(episodeId, _access.GetPublishedVideoIds());
        Assert.DoesNotContain("series-1", _access.GetPublishedSeriesIds());
    }

    [Fact]
    public void UnpublishedOrUncategorizedSeriesAreNotIncludedInPublicSeriesList()
    {
        CreateRootAndSeriesCategory();
        _categories.Save(new SaveCatalogCategoryRequest("hidden-series-category", "root", "مسلسلات مخفية", "Series", null, false));

        _series.SaveSeries(new SaveSeriesRequest(
            SeriesId: "published-series",
            Title: "مسلسل منشور",
            Description: null,
            Year: null,
            Genre: null,
            PosterPath: null,
            IsPublished: true,
            CategoryId: "series-category"));
        _series.SaveSeries(new SaveSeriesRequest(
            SeriesId: "hidden-series",
            Title: "مسلسل مخفي",
            Description: null,
            Year: null,
            Genre: null,
            PosterPath: null,
            IsPublished: true,
            CategoryId: "hidden-series-category"));

        var ids = _access.GetPublishedSeriesIds();
        Assert.Contains("published-series", ids);
        Assert.DoesNotContain("hidden-series", ids);
    }

    private void CreateRootAndMovieCategory()
    {
        _categories.Save(new SaveCatalogCategoryRequest("root", null, "القسم الرئيسي", "Group", null, true));
        _categories.Save(new SaveCatalogCategoryRequest("movies", "root", "أفلام", "Movie", null, true));
    }

    private void CreateRootAndSeriesCategory()
    {
        _categories.Save(new SaveCatalogCategoryRequest("root", null, "القسم الرئيسي", "Group", null, true));
        _categories.Save(new SaveCatalogCategoryRequest("series-category", "root", "مسلسلات", "Series", null, true));
    }

    private void SaveMovie(string videoId, bool isPublished)
    {
        _metadata.Save(new SaveVideoMetadataRequest(
            VideoId: videoId,
            Title: "فيلم اختباري",
            Description: "",
            Year: null,
            Genre: "",
            Kind: "Movie",
            PosterPath: null,
            IsPublished: isPublished,
            CategoryId: "movies"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
