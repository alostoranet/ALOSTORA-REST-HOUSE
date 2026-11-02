using Alostora.RestHouse;
using Microsoft.Data.Sqlite;
using Xunit;

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
