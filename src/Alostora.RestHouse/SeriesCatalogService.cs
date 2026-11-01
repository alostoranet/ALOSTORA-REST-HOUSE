using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record SeriesRecord(string SeriesId, string Title, string Description, int? Year, string Genre, string? PosterPath);
public sealed record SeasonRecord(long SeasonId, string SeriesId, int SeasonNumber, string Title);
public sealed record EpisodeRecord(long EpisodeId, long SeasonId, string VideoId, int EpisodeNumber, string Title);
public sealed record SaveSeriesRequest(string SeriesId, string Title, string? Description, int? Year, string? Genre, string? PosterPath);
public sealed record SaveSeasonRequest(string SeriesId, int SeasonNumber, string? Title);
public sealed record SaveEpisodeRequest(long SeasonId, string VideoId, int EpisodeNumber, string? Title);

public sealed class SeriesCatalogService
{
    private readonly string _connectionString;
    public SeriesCatalogService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 10, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
          CREATE TABLE IF NOT EXISTS SeriesCatalog(
            SeriesId TEXT PRIMARY KEY, Title TEXT NOT NULL, Description TEXT NOT NULL DEFAULT '',
            Year INTEGER NULL, Genre TEXT NOT NULL DEFAULT '', PosterPath TEXT NULL, UpdatedUtc TEXT NOT NULL
          );
          CREATE TABLE IF NOT EXISTS SeriesSeasons(
            SeasonId INTEGER PRIMARY KEY AUTOINCREMENT, SeriesId TEXT NOT NULL REFERENCES SeriesCatalog(SeriesId) ON DELETE CASCADE,
            SeasonNumber INTEGER NOT NULL CHECK(SeasonNumber > 0), Title TEXT NOT NULL DEFAULT '',
            UNIQUE(SeriesId, SeasonNumber)
          );
          CREATE TABLE IF NOT EXISTS SeriesEpisodes(
            EpisodeId INTEGER PRIMARY KEY AUTOINCREMENT, SeasonId INTEGER NOT NULL REFERENCES SeriesSeasons(SeasonId) ON DELETE CASCADE,
            VideoId TEXT NOT NULL UNIQUE, EpisodeNumber INTEGER NOT NULL CHECK(EpisodeNumber > 0), Title TEXT NOT NULL DEFAULT '',
            UNIQUE(SeasonId, EpisodeNumber)
          );
          CREATE INDEX IF NOT EXISTS IX_SeriesSeasons_SeriesId ON SeriesSeasons(SeriesId);
          CREATE INDEX IF NOT EXISTS IX_SeriesEpisodes_SeasonId ON SeriesEpisodes(SeasonId);
          """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    public IReadOnlyList<SeriesRecord> ListSeries()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath FROM SeriesCatalog ORDER BY Title COLLATE NOCASE";
        using var r=cmd.ExecuteReader(); var rows=new List<SeriesRecord>();
        while(r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5)));
        return rows;
    }
    public IReadOnlyList<SeasonRecord> ListSeasons(string seriesId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SeasonId,SeriesId,SeasonNumber,Title FROM SeriesSeasons WHERE SeriesId=$id ORDER BY SeasonNumber";
        cmd.Parameters.AddWithValue("$id", seriesId);
        using var r = cmd.ExecuteReader(); var rows = new List<SeasonRecord>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        return rows;
    }

    public SeriesRecord? GetSeries(string id)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath FROM SeriesCatalog WHERE SeriesId=$id"; cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5)):null;
    }
    public void SaveSeries(SaveSeriesRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="""INSERT INTO SeriesCatalog(SeriesId,Title,Description,Year,Genre,PosterPath,UpdatedUtc) VALUES($id,$title,$description,$year,$genre,$poster,$updated) ON CONFLICT(SeriesId) DO UPDATE SET Title=excluded.Title,Description=excluded.Description,Year=excluded.Year,Genre=excluded.Genre,PosterPath=COALESCE(excluded.PosterPath, SeriesCatalog.PosterPath),UpdatedUtc=excluded.UpdatedUtc""";
        cmd.Parameters.AddWithValue("$id",x.SeriesId); cmd.Parameters.AddWithValue("$title",x.Title.Trim()); cmd.Parameters.AddWithValue("$description",x.Description??""); cmd.Parameters.AddWithValue("$year",(object?)x.Year??DBNull.Value); cmd.Parameters.AddWithValue("$genre",x.Genre??""); cmd.Parameters.AddWithValue("$poster",(object?)x.PosterPath??DBNull.Value); cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public long AddSeason(SaveSeasonRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO SeriesSeasons(SeriesId,SeasonNumber,Title) VALUES($series,$number,$title); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$series",x.SeriesId); cmd.Parameters.AddWithValue("$number",x.SeasonNumber); cmd.Parameters.AddWithValue("$title",x.Title??"");
        return (long)cmd.ExecuteScalar()!;
    }
    public long AddEpisode(SaveEpisodeRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO SeriesEpisodes(SeasonId,VideoId,EpisodeNumber,Title) VALUES($season,$video,$number,$title); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$season",x.SeasonId); cmd.Parameters.AddWithValue("$video",x.VideoId); cmd.Parameters.AddWithValue("$number",x.EpisodeNumber); cmd.Parameters.AddWithValue("$title",x.Title??"");
        return (long)cmd.ExecuteScalar()!;
    }
    public object GetCatalog(LibraryService library, VideoMetadataService metadata)
    {
        // Load each source into memory before composing the response. This avoids
        // opening nested SQLite readers on the same connection while one is active.
        var files = library.List().ToDictionary(x => x.Id);
        var metadataById = metadata.List().ToDictionary(x => x.VideoId);
        var seriesRows = ListSeries();

        var seasonMap = new Dictionary<long, (SeasonRecord Season, List<EpisodeRecord> Episodes)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ss.SeasonId, ss.SeriesId, ss.SeasonNumber, ss.Title,
                       se.EpisodeId, se.VideoId, se.EpisodeNumber, se.Title
                FROM SeriesSeasons ss
                LEFT JOIN SeriesEpisodes se ON se.SeasonId = ss.SeasonId
                ORDER BY ss.SeriesId, ss.SeasonNumber, se.EpisodeNumber
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var seasonId = r.GetInt64(0);
                if (!seasonMap.ContainsKey(seasonId))
                {
                    var season = new SeasonRecord(seasonId, r.GetString(1), r.GetInt32(2), r.GetString(3));
                    seasonMap.Add(seasonId, (season, new List<EpisodeRecord>()));
                }

                if (!r.IsDBNull(4))
                {
                    seasonMap[seasonId].Episodes.Add(new EpisodeRecord(
                        r.GetInt64(4), seasonId, r.GetString(5), r.GetInt32(6), r.GetString(7)));
                }
            }
        }

        return seriesRows.Select(series =>
        {
            var seasons = seasonMap.Values
                .Where(x => x.Season.SeriesId == series.SeriesId)
                .OrderBy(x => x.Season.SeasonNumber)
                .Select(x => new
                {
                    seasonId = x.Season.SeasonId,
                    seriesId = x.Season.SeriesId,
                    seasonNumber = x.Season.SeasonNumber,
                    title = x.Season.Title,
                    episodes = x.Episodes
                        .Where(episode => files.ContainsKey(episode.VideoId))
                        .Select(episode =>
                        {
                            var file = files[episode.VideoId];
                            metadataById.TryGetValue(episode.VideoId, out var videoMetadata);
                            var title = string.IsNullOrWhiteSpace(episode.Title)
                                ? (videoMetadata?.Title ?? file.Title)
                                : episode.Title;
                            return new
                            {
                                episodeId = episode.EpisodeId,
                                videoId = episode.VideoId,
                                episodeNumber = episode.EpisodeNumber,
                                title,
                                url = file.Url
                            };
                        })
                        .ToArray()
                })
                .ToArray();

            return new
            {
                seriesId = series.SeriesId,
                title = series.Title,
                description = series.Description,
                year = series.Year,
                genre = series.Genre,
                posterPath = series.PosterPath,
                seasons
            };
        }).ToArray();
    }
}
