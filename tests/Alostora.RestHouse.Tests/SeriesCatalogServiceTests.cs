using Alostora.RestHouse;
using Microsoft.Data.Sqlite;
using Xunit;
using System.Text.Json;

namespace Alostora.RestHouse.Tests;

public sealed class SeriesCatalogServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly SeriesCatalogService _series;

    public SeriesCatalogServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "alostora-series-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "test.db");
        _series = new SeriesCatalogService(_databasePath);
        _series.SaveSeries(new SaveSeriesRequest(
            "series-test", "مسلسل اختباري", "", 2026, "", null, IsPublished: false));
    }

    [Fact]
    public void PublicSeriesCatalogOmitsEpisodesWhoseVideosAreNotPublished()
    {
        var libraryRoot = Path.Combine(_root, "public-library");
        Directory.CreateDirectory(libraryRoot);
        File.WriteAllBytes(Path.Combine(libraryRoot, "published.mp4"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(libraryRoot, "unpublished.mp4"), new byte[] { 4, 5, 6 });

        using var library = new LibraryService(libraryRoot, _databasePath);
        var videos = library.List().ToDictionary(x => x.FileName);
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, videos["published.mp4"].Id, 1, "حلقة منشورة"));
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, videos["unpublished.mp4"].Id, 2, "حلقة غير منشورة"));

        var metadata = new VideoMetadataService(_databasePath);
        var catalog = _series.GetCatalog(
            library,
            metadata,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "series-test" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { videos["published.mp4"].Id });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(catalog));
        var episodes = document.RootElement[0].GetProperty("seasons")[0].GetProperty("episodes");

        var episode = Assert.Single(episodes.EnumerateArray());
        Assert.Equal("حلقة منشورة", episode.GetProperty("title").GetString());
        Assert.Equal(videos["published.mp4"].Id, episode.GetProperty("videoId").GetString());
    }

    [Fact]
    public void SeriesCanUsePartsAndStoreVideosDirectlyUnderTheSeries()
    {
        _series.SaveSeries(new SaveSeriesRequest(
            "series-test", "مسلسل الأجزاء", "", 2026, "", null, IsPublished: false, OrganizationType: "Parts"));

        var series = Assert.Single(_series.ListSeries());
        Assert.Equal("Parts", series.OrganizationType);

        _series.SetVideoPlacement("series-test", null, "aaaaaaaaaaaaaaaaaaaaaaaa", null, "فيديو مباشر");
        var placement = _series.GetVideoPlacement("aaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.NotNull(placement);
        Assert.Equal("series-test", placement.SeriesId);
        Assert.Null(placement.SeasonId);
        Assert.Equal("فيديو مباشر", placement.Title);
    }

    [Fact]
    public void VideoCanMoveBetweenDirectSeriesPlacementAndPartWithoutDeletingTheFile()
    {
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الجزء الأول"));
        const string videoId = "bbbbbbbbbbbbbbbbbbbbbbbb";

        _series.SetVideoPlacement("series-test", null, videoId, null, "مقدمة");
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, videoId, 3, "الحلقة الثالثة"));

        var placement = _series.GetVideoPlacement(videoId);
        Assert.NotNull(placement);
        Assert.Equal(seasonId, placement.SeasonId);
        Assert.Equal(3, placement.EpisodeNumber);
        Assert.Equal("الحلقة الثالثة", placement.Title);

        _series.SetVideoPlacement(null, null, videoId, null, null);
        Assert.Null(_series.GetVideoPlacement(videoId));
        Assert.NotNull(_series.GetSeries("series-test"));
    }

    [Fact]
    public void UpdateEpisodeChangesNumberAndTitleWithoutChangingVideoLink()
    {
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        var episodeId = _series.AddEpisode(new SaveEpisodeRequest(seasonId, "aaaaaaaaaaaaaaaaaaaaaaaa", 1, "قديم"));

        _series.UpdateEpisode(episodeId, 7, "  الاسم الجديد  ");

        var episode = Assert.Single(_series.ListEpisodes(seasonId));
        Assert.Equal(episodeId, episode.EpisodeId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", episode.VideoId);
        Assert.Equal(7, episode.EpisodeNumber);
        Assert.Equal("الاسم الجديد", episode.Title);
    }

    [Fact]
    public void UnlinkEpisodeRemovesOnlyTheSeriesLinkAndKeepsOriginalVideoFile()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        var videoPath = Path.Combine(libraryRoot, "episode.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 1, 2, 3, 4, 5 });

        using var library = new LibraryService(libraryRoot, _databasePath);
        var video = Assert.Single(library.List());
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        var episodeId = _series.AddEpisode(new SaveEpisodeRequest(seasonId, video.Id, 1, "الحلقة"));

        var unlinkedVideoId = _series.UnlinkEpisode(episodeId);

        Assert.Equal(video.Id, unlinkedVideoId);
        Assert.Empty(_series.ListEpisodes(seasonId));
        Assert.True(File.Exists(videoPath));
        Assert.NotNull(library.Resolve(video.Id));
    }

    [Fact]
    public void MissingPhysicalVideoFileIsReportedWithoutRemovingEpisodeRecord()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        var videoPath = Path.Combine(libraryRoot, "missing-later.mp4");
        File.WriteAllBytes(videoPath, new byte[] { 9, 8, 7 });

        string videoId;
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        using (var initialLibrary = new LibraryService(libraryRoot, _databasePath))
        {
            videoId = Assert.Single(initialLibrary.List()).Id;
            _series.AddEpisode(new SaveEpisodeRequest(seasonId, videoId, 3, "حلقة ملفها مفقود"));
        }

        File.Delete(videoPath);
        using var rescannedLibrary = new LibraryService(libraryRoot, _databasePath);
        var missing = Assert.Single(_series.ListMissingEpisodes(rescannedLibrary));

        Assert.Equal(videoId, missing.VideoId);
        Assert.Equal(3, missing.EpisodeNumber);
        Assert.Single(_series.ListEpisodes(seasonId));
    }

    [Fact]
    public void DuplicateEpisodeNumberAndDuplicateVideoLinksAreRejected()
    {
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, "aaaaaaaaaaaaaaaaaaaaaaaa", 1, "الأولى"));

        Assert.Throws<SqliteException>(() =>
            _series.AddEpisode(new SaveEpisodeRequest(seasonId, "bbbbbbbbbbbbbbbbbbbbbbbb", 1, "رقم مكرر")));

        Assert.Throws<SqliteException>(() =>
            _series.AddEpisode(new SaveEpisodeRequest(seasonId, "aaaaaaaaaaaaaaaaaaaaaaaa", 2, "ملف مربوط مسبقاً")));
    }

    [Fact]
    public void EditingEpisodeToAnExistingNumberIsRejectedWithoutChangingTheOriginal()
    {
        var seasonId = _series.AddSeason(new SaveSeasonRequest("series-test", 1, "الموسم الأول"));
        var firstId = _series.AddEpisode(new SaveEpisodeRequest(seasonId, "aaaaaaaaaaaaaaaaaaaaaaaa", 1, "الأولى"));
        _series.AddEpisode(new SaveEpisodeRequest(seasonId, "bbbbbbbbbbbbbbbbbbbbbbbb", 2, "الثانية"));

        Assert.Throws<SqliteException>(() => _series.UpdateEpisode(firstId, 2, "تعارض"));

        var first = _series.ListEpisodes(seasonId).Single(x => x.EpisodeId == firstId);
        Assert.Equal(1, first.EpisodeNumber);
        Assert.Equal("الأولى", first.Title);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
