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
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();
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
        cmd.CommandText="""INSERT INTO SeriesCatalog(SeriesId,Title,Description,Year,Genre,PosterPath,UpdatedUtc) VALUES($id,$title,$description,$year,$genre,$poster,$updated) ON CONFLICT(SeriesId) DO UPDATE SET Title=excluded.Title,Description=excluded.Description,Year=excluded.Year,Genre=excluded.Genre,PosterPath=excluded.PosterPath,UpdatedUtc=excluded.UpdatedUtc""";
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
        var files=library.List().ToDictionary(x=>x.Id);
        using var c=Open();
        var series=ListSeries().Select(s=> {
            var seasons=new List<object>();
            using var sc=c.CreateCommand(); sc.CommandText="SELECT SeasonId,SeriesId,SeasonNumber,Title FROM SeriesSeasons WHERE SeriesId=$id ORDER BY SeasonNumber"; sc.Parameters.AddWithValue("$id",s.SeriesId);
            using var sr=sc.ExecuteReader();
            while(sr.Read()) {
                var seasonId=sr.GetInt64(0); var episodes=new List<object>();
                using var ec=c.CreateCommand(); ec.CommandText="SELECT EpisodeId,SeasonId,VideoId,EpisodeNumber,Title FROM SeriesEpisodes WHERE SeasonId=$id ORDER BY EpisodeNumber"; ec.Parameters.AddWithValue("$id",seasonId);
                using var er=ec.ExecuteReader();
                while(er.Read()) { var vid=er.GetString(2); if(!files.TryGetValue(vid,out var f)) continue; var m=metadata.Get(vid); episodes.Add(new { episodeId=er.GetInt64(0),videoId=vid,episodeNumber=er.GetInt32(3),title=string.IsNullOrWhiteSpace(er.GetString(4))?(m?.Title??f.Title):er.GetString(4),url=f.Url }); }
                seasons.Add(new { seasonId,seriesId=sr.GetString(1),seasonNumber=sr.GetInt32(2),title=sr.GetString(3),episodes });
            }
            return new { seriesId=s.SeriesId,title=s.Title,description=s.Description,year=s.Year,genre=s.Genre,posterPath=s.PosterPath,seasons };
        }).ToArray();
        return series;
    }
}
