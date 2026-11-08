using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record SeriesRecord(string SeriesId, string Title, string Description, int? Year, string Genre, string? PosterPath, string? FolderPath, string? CategoryId, string Section, bool IsPublished, string OrganizationType, string UpdatedUtc);
public sealed record SeasonRecord(long SeasonId, string SeriesId, int SeasonNumber, string Title, string? FolderPath);
public sealed record EpisodeRecord(long EpisodeId, long SeasonId, string VideoId, int EpisodeNumber, string Title, string? PosterPath = null);
public sealed record DirectVideoRecord(string VideoId, string Title);
public sealed record MissingEpisodeRecord(long EpisodeId, string SeriesId, string SeriesTitle, int SeasonNumber, int EpisodeNumber, string Title, string VideoId);
public sealed record SaveSeriesRequest(string SeriesId, string Title, string? Description, int? Year, string? Genre, string? PosterPath, string? FolderPath = null, string? Section = null, bool IsPublished = false, string? CategoryId = null, string? OrganizationType = "Seasons");
public sealed record VideoPlacementRecord(string SeriesId, long? SeasonId, int? EpisodeNumber, string? Title);
public sealed record SaveSeasonRequest(string SeriesId, int SeasonNumber, string? Title, string? FolderPath = null);
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
            Year INTEGER NULL, Genre TEXT NOT NULL DEFAULT '', PosterPath TEXT NULL, FolderPath TEXT NULL, CategoryId TEXT NULL,
            Section TEXT NOT NULL DEFAULT 'عام', IsPublished INTEGER NOT NULL DEFAULT 1, UpdatedUtc TEXT NOT NULL
          );
          CREATE TABLE IF NOT EXISTS SeriesSeasons(
            SeasonId INTEGER PRIMARY KEY AUTOINCREMENT, SeriesId TEXT NOT NULL REFERENCES SeriesCatalog(SeriesId) ON DELETE CASCADE,
            SeasonNumber INTEGER NOT NULL CHECK(SeasonNumber > 0), Title TEXT NOT NULL DEFAULT '', FolderPath TEXT NULL,
            UNIQUE(SeriesId, SeasonNumber)
          );
          CREATE TABLE IF NOT EXISTS SeriesEpisodes(
            EpisodeId INTEGER PRIMARY KEY AUTOINCREMENT, SeasonId INTEGER NOT NULL REFERENCES SeriesSeasons(SeasonId) ON DELETE CASCADE,
            VideoId TEXT NOT NULL UNIQUE, EpisodeNumber INTEGER NOT NULL CHECK(EpisodeNumber > 0), Title TEXT NOT NULL DEFAULT '', PosterPath TEXT NULL,
            UNIQUE(SeasonId, EpisodeNumber)
          );
          CREATE INDEX IF NOT EXISTS IX_SeriesSeasons_SeriesId ON SeriesSeasons(SeriesId);
          CREATE INDEX IF NOT EXISTS IX_SeriesEpisodes_SeasonId ON SeriesEpisodes(SeasonId);
          CREATE TABLE IF NOT EXISTS SeriesDirectVideos(
            VideoId TEXT PRIMARY KEY, SeriesId TEXT NOT NULL REFERENCES SeriesCatalog(SeriesId) ON DELETE CASCADE,
            Title TEXT NOT NULL DEFAULT ''
          );
          CREATE INDEX IF NOT EXISTS IX_SeriesDirectVideos_SeriesId ON SeriesDirectVideos(SeriesId);
          """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, "SeriesCatalog", "FolderPath", "TEXT NULL");
        EnsureColumn(c, "SeriesCatalog", "CategoryId", "TEXT NULL");
        EnsureColumn(c, "SeriesCatalog", "Section", "TEXT NOT NULL DEFAULT 'عام'");
        EnsureColumn(c, "SeriesCatalog", "IsPublished", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(c, "SeriesCatalog", "OrganizationType", "TEXT NOT NULL DEFAULT 'Seasons'");
        EnsureColumn(c, "SeriesSeasons", "FolderPath", "TEXT NULL");
        EnsureColumn(c, "SeriesEpisodes", "PosterPath", "TEXT NULL");
        EnsureColumn(c, "SeriesDirectVideos", "EpisodeNumber", "INTEGER NULL");
        using (var directIndex = c.CreateCommand())
        {
            directIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_SeriesDirectVideos_SeriesEpisode ON SeriesDirectVideos(SeriesId, EpisodeNumber) WHERE EpisodeNumber IS NOT NULL";
            directIndex.ExecuteNonQuery();
        }
    }
    private static void EnsureColumn(SqliteConnection c, string table, string column, string type)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}"; alter.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    public IReadOnlyList<SeriesRecord> ListSeries()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath,CategoryId,Section,IsPublished,OrganizationType,UpdatedUtc FROM SeriesCatalog ORDER BY Title COLLATE NOCASE";
        using var r=cmd.ExecuteReader(); var rows=new List<SeriesRecord>();
        while(r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetString(8),r.GetInt32(9)!=0,r.GetString(10),r.GetString(11)));
        return rows;
    }
    public IReadOnlyList<SeasonRecord> ListSeasons(string seriesId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SeasonId,SeriesId,SeasonNumber,Title,FolderPath FROM SeriesSeasons WHERE SeriesId=$id ORDER BY SeasonNumber";
        cmd.Parameters.AddWithValue("$id", seriesId);
        using var r = cmd.ExecuteReader(); var rows = new List<SeasonRecord>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    public SeasonRecord? GetSeason(long seasonId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SeasonId,SeriesId,SeasonNumber,Title,FolderPath FROM SeriesSeasons WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$id", seasonId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SeasonRecord(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)) : null;
    }

    public IReadOnlyList<EpisodeRecord> ListEpisodes(long seasonId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EpisodeId,SeasonId,VideoId,EpisodeNumber,Title,PosterPath FROM SeriesEpisodes WHERE SeasonId=$id ORDER BY EpisodeNumber";
        cmd.Parameters.AddWithValue("$id", seasonId);
        using var r = cmd.ExecuteReader(); var rows = new List<EpisodeRecord>();
        while (r.Read()) rows.Add(new EpisodeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return rows;
    }

    public SeriesRecord? GetSeries(string id)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath,CategoryId,Section,IsPublished,OrganizationType,UpdatedUtc FROM SeriesCatalog WHERE SeriesId=$id"; cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetString(8),r.GetInt32(9)!=0,r.GetString(10),r.GetString(11)):null;
    }
    public void SaveSeries(SaveSeriesRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="""INSERT INTO SeriesCatalog(SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath,CategoryId,Section,IsPublished,OrganizationType,UpdatedUtc) VALUES($id,$title,$description,$year,$genre,$poster,$folder,$categoryId,$section,$published,$organizationType,$updated) ON CONFLICT(SeriesId) DO UPDATE SET Title=excluded.Title,Description=excluded.Description,Year=excluded.Year,Genre=excluded.Genre,PosterPath=COALESCE(excluded.PosterPath, SeriesCatalog.PosterPath),FolderPath=excluded.FolderPath,CategoryId=excluded.CategoryId,Section=excluded.Section,IsPublished=excluded.IsPublished,OrganizationType=excluded.OrganizationType,UpdatedUtc=excluded.UpdatedUtc""";
        cmd.Parameters.AddWithValue("$id",x.SeriesId); cmd.Parameters.AddWithValue("$title",x.Title.Trim()); cmd.Parameters.AddWithValue("$description",x.Description??""); cmd.Parameters.AddWithValue("$year",(object?)x.Year??DBNull.Value); cmd.Parameters.AddWithValue("$genre",x.Genre??""); cmd.Parameters.AddWithValue("$poster",(object?)x.PosterPath??DBNull.Value); cmd.Parameters.AddWithValue("$folder",(object?)x.FolderPath??DBNull.Value); cmd.Parameters.AddWithValue("$categoryId",(object?)x.CategoryId??DBNull.Value); cmd.Parameters.AddWithValue("$section",string.IsNullOrWhiteSpace(x.Section)?"عام":x.Section.Trim()); cmd.Parameters.AddWithValue("$published",x.IsPublished?1:0); cmd.Parameters.AddWithValue("$organizationType",x.OrganizationType is "None" or "Parts" ? x.OrganizationType : "Seasons"); cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public VideoPlacementRecord? GetVideoPlacement(string videoId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT ss.SeriesId, se.SeasonId, se.EpisodeNumber, se.Title
            FROM SeriesEpisodes se JOIN SeriesSeasons ss ON ss.SeasonId=se.SeasonId
            WHERE se.VideoId=$video
            UNION ALL
            SELECT SeriesId, NULL, EpisodeNumber, Title FROM SeriesDirectVideos WHERE VideoId=$video
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$video", videoId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new VideoPlacementRecord(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)) : null;
    }

    public void SetVideoPlacement(string? seriesId, long? seasonId, string videoId, int? episodeNumber, string? title)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            using var removeEpisode = c.CreateCommand();
            removeEpisode.Transaction = transaction;
            removeEpisode.CommandText = "DELETE FROM SeriesEpisodes WHERE VideoId=$video";
            removeEpisode.Parameters.AddWithValue("$video", videoId);
            removeEpisode.ExecuteNonQuery();
            using var removeDirect = c.CreateCommand();
            removeDirect.Transaction = transaction;
            removeDirect.CommandText = "DELETE FROM SeriesDirectVideos WHERE VideoId=$video";
            removeDirect.Parameters.AddWithValue("$video", videoId);
            removeDirect.ExecuteNonQuery();
            transaction.Commit();
            return;
        }

        using (var exists = c.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM SeriesCatalog WHERE SeriesId=$series";
            exists.Parameters.AddWithValue("$series", seriesId);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) throw new KeyNotFoundException("المسلسل المحدد غير موجود.");
        }

        using (var existingPlacement = c.CreateCommand())
        {
            existingPlacement.Transaction = transaction;
            existingPlacement.CommandText = """
                SELECT SeriesId FROM SeriesEpisodes se
                JOIN SeriesSeasons ss ON ss.SeasonId=se.SeasonId
                WHERE se.VideoId=$video
                UNION ALL
                SELECT SeriesId FROM SeriesDirectVideos WHERE VideoId=$video
                LIMIT 1
                """;
            existingPlacement.Parameters.AddWithValue("$video", videoId);
            var existingSeriesId = existingPlacement.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existingSeriesId) &&
                !string.Equals(existingSeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("الفيديو مربوط بالفعل بمسلسل آخر. افصل ارتباطه أولاً قبل نقله.");
        }

        if (seasonId.HasValue)
        {
            if (episodeNumber is null or < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(episodeNumber), "رقم الحلقة أو الفيديو داخل الموسم/الجزء غير صحيح.");
            using var season = c.CreateCommand();
            season.Transaction = transaction;
            season.CommandText = "SELECT COUNT(*) FROM SeriesSeasons WHERE SeasonId=$season AND SeriesId=$series";
            season.Parameters.AddWithValue("$season", seasonId.Value);
            season.Parameters.AddWithValue("$series", seriesId);
            if (Convert.ToInt32(season.ExecuteScalar()) == 0) throw new InvalidOperationException("الموسم أو الجزء لا يتبع المسلسل المحدد.");
            using var removeDirect = c.CreateCommand();
            removeDirect.Transaction = transaction;
            removeDirect.CommandText = "DELETE FROM SeriesDirectVideos WHERE VideoId=$video";
            removeDirect.Parameters.AddWithValue("$video", videoId);
            removeDirect.ExecuteNonQuery();
            using var upsert = c.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO SeriesEpisodes(SeasonId,VideoId,EpisodeNumber,Title)
                VALUES($season,$video,$number,$title)
                ON CONFLICT(VideoId) DO UPDATE SET SeasonId=excluded.SeasonId,EpisodeNumber=excluded.EpisodeNumber,Title=excluded.Title
                """;
            upsert.Parameters.AddWithValue("$season", seasonId.Value);
            upsert.Parameters.AddWithValue("$video", videoId);
            upsert.Parameters.AddWithValue("$number", episodeNumber.Value);
            upsert.Parameters.AddWithValue("$title", title?.Trim() ?? "");
            upsert.ExecuteNonQuery();
        }
        else
        {
            using var removeEpisode = c.CreateCommand();
            removeEpisode.Transaction = transaction;
            removeEpisode.CommandText = "DELETE FROM SeriesEpisodes WHERE VideoId=$video";
            removeEpisode.Parameters.AddWithValue("$video", videoId);
            removeEpisode.ExecuteNonQuery();
            using var direct = c.CreateCommand();
            direct.Transaction = transaction;
            direct.CommandText = """
                INSERT INTO SeriesDirectVideos(VideoId,SeriesId,EpisodeNumber,Title) VALUES($video,$series,NULL,$title)
                ON CONFLICT(VideoId) DO UPDATE SET SeriesId=excluded.SeriesId,EpisodeNumber=NULL,Title=excluded.Title
                """;
            direct.Parameters.AddWithValue("$video", videoId);
            direct.Parameters.AddWithValue("$series", seriesId);
            direct.Parameters.AddWithValue("$title", title?.Trim() ?? "");
            direct.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<DirectVideoRecord> ListDirectVideos(string seriesId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT VideoId, Title FROM SeriesDirectVideos WHERE SeriesId=$series ORDER BY COALESCE(EpisodeNumber,2147483647), Title COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$series", seriesId);
        using var r = cmd.ExecuteReader();
        var rows = new List<DirectVideoRecord>();
        while (r.Read()) rows.Add(new DirectVideoRecord(r.GetString(0), r.GetString(1)));
        return rows;
    }

    public void UnlinkDirectVideo(string seriesId, string videoId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM SeriesDirectVideos WHERE SeriesId=$series AND VideoId=$video";
        cmd.Parameters.AddWithValue("$series", seriesId);
        cmd.Parameters.AddWithValue("$video", videoId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الفيديو غير مربوط مباشرة بهذا المسلسل.");
    }
    public IReadOnlyList<int> ListDirectEpisodeNumbers(string seriesId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EpisodeNumber FROM SeriesDirectVideos WHERE SeriesId=$series AND EpisodeNumber IS NOT NULL";
        cmd.Parameters.AddWithValue("$series", seriesId);
        using var r = cmd.ExecuteReader();
        var numbers = new List<int>();
        while (r.Read()) numbers.Add(r.GetInt32(0));
        return numbers;
    }

    public void SetDirectEpisode(string seriesId, string videoId, int episodeNumber, string? title)
    {
        if (episodeNumber < 1 || episodeNumber > 10000)
            throw new ArgumentOutOfRangeException(nameof(episodeNumber), "رقم الحلقة يجب أن يكون بين 1 و10000.");
        using var c = Open();
        using var transaction = c.BeginTransaction();
        using (var exists = c.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM SeriesCatalog WHERE SeriesId=$series";
            exists.Parameters.AddWithValue("$series", seriesId);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) throw new KeyNotFoundException("المسلسل غير موجود.");
        }
        using (var removeEpisode = c.CreateCommand())
        {
            removeEpisode.Transaction = transaction;
            removeEpisode.CommandText = "DELETE FROM SeriesEpisodes WHERE VideoId=$video";
            removeEpisode.Parameters.AddWithValue("$video", videoId);
            removeEpisode.ExecuteNonQuery();
        }
        using (var upsert = c.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO SeriesDirectVideos(VideoId,SeriesId,EpisodeNumber,Title)
                VALUES($video,$series,$number,$title)
                ON CONFLICT(VideoId) DO UPDATE SET SeriesId=excluded.SeriesId,EpisodeNumber=excluded.EpisodeNumber,Title=excluded.Title
                """;
            upsert.Parameters.AddWithValue("$video", videoId);
            upsert.Parameters.AddWithValue("$series", seriesId);
            upsert.Parameters.AddWithValue("$number", episodeNumber);
            upsert.Parameters.AddWithValue("$title", title?.Trim() ?? "");
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public long AddSeason(SaveSeasonRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO SeriesSeasons(SeriesId,SeasonNumber,Title,FolderPath) VALUES($series,$number,$title,$folder); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$series",x.SeriesId); cmd.Parameters.AddWithValue("$number",x.SeasonNumber); cmd.Parameters.AddWithValue("$title",x.Title??""); cmd.Parameters.AddWithValue("$folder",(object?)x.FolderPath??DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }
    public void DeleteSeries(string seriesId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM SeriesCatalog WHERE SeriesId=$id";
        cmd.Parameters.AddWithValue("$id", seriesId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("المسلسل غير موجود");
    }

    public void DeleteSeason(long seasonId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM SeriesSeasons WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$id", seasonId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الموسم غير موجود");
    }

    public void UpdateSeasonFolder(long seasonId, string? folderPath)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SeriesSeasons SET FolderPath=$folder WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$folder", (object?)folderPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", seasonId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الموسم غير موجود");
    }

    public long AddEpisode(SaveEpisodeRequest x)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO SeriesEpisodes(SeasonId,VideoId,EpisodeNumber,Title) VALUES($season,$video,$number,$title); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$season", x.SeasonId);
        cmd.Parameters.AddWithValue("$video", x.VideoId);
        cmd.Parameters.AddWithValue("$number", x.EpisodeNumber);
        cmd.Parameters.AddWithValue("$title", x.Title ?? "");
        var episodeId = (long)cmd.ExecuteScalar()!;
        using var removeDirect = c.CreateCommand();
        removeDirect.Transaction = transaction;
        removeDirect.CommandText = "DELETE FROM SeriesDirectVideos WHERE VideoId=$video";
        removeDirect.Parameters.AddWithValue("$video", x.VideoId);
        removeDirect.ExecuteNonQuery();
        transaction.Commit();
        return episodeId;
    }
    public string UpdateEpisodeVideo(long episodeId, string videoId)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        string oldVideoId;
        using (var find = c.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM SeriesEpisodes WHERE EpisodeId=$id";
            find.Parameters.AddWithValue("$id", episodeId);
            oldVideoId = find.ExecuteScalar() as string ?? throw new KeyNotFoundException("الحلقة غير موجودة");
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE SeriesEpisodes SET VideoId=$video WHERE EpisodeId=$id";
            update.Parameters.AddWithValue("$video", videoId);
            update.Parameters.AddWithValue("$id", episodeId);
            update.ExecuteNonQuery();
        }
        using (var removeDirect = c.CreateCommand())
        {
            removeDirect.Transaction = transaction;
            removeDirect.CommandText = "DELETE FROM SeriesDirectVideos WHERE VideoId=$video";
            removeDirect.Parameters.AddWithValue("$video", videoId);
            removeDirect.ExecuteNonQuery();
        }
        transaction.Commit();
        return oldVideoId;
    }

    public string? GetEpisodePoster(long episodeId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PosterPath FROM SeriesEpisodes WHERE EpisodeId=$id";
        cmd.Parameters.AddWithValue("$id", episodeId);
        return cmd.ExecuteScalar() as string;
    }

    public void SetEpisodePoster(long episodeId, string posterPath)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SeriesEpisodes SET PosterPath=$path WHERE EpisodeId=$id";
        cmd.Parameters.AddWithValue("$path", posterPath);
        cmd.Parameters.AddWithValue("$id", episodeId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الحلقة غير موجودة");
    }

    public void UpdateEpisode(long episodeId, int episodeNumber, string? title)
    {
        if (episodeNumber < 1 || episodeNumber > 10000)
            throw new ArgumentOutOfRangeException(nameof(episodeNumber), "رقم الحلقة يجب أن يكون بين 1 و10000.");

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SeriesEpisodes SET EpisodeNumber=$number, Title=$title WHERE EpisodeId=$id";
        cmd.Parameters.AddWithValue("$number", episodeNumber);
        cmd.Parameters.AddWithValue("$title", title?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$id", episodeId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException("الحلقة غير موجودة");
    }

    public string UnlinkEpisode(long episodeId)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        string videoId;
        using (var find = c.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM SeriesEpisodes WHERE EpisodeId=$id";
            find.Parameters.AddWithValue("$id", episodeId);
            videoId = find.ExecuteScalar() as string ?? throw new KeyNotFoundException("الحلقة غير موجودة");
        }

        using (var delete = c.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM SeriesEpisodes WHERE EpisodeId=$id";
            delete.Parameters.AddWithValue("$id", episodeId);
            if (delete.ExecuteNonQuery() == 0)
                throw new KeyNotFoundException("الحلقة غير موجودة");
        }

        transaction.Commit();
        return videoId;
    }

    public IReadOnlyList<MissingEpisodeRecord> ListMissingEpisodes(LibraryService library)
    {
        var available = library.List().Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT se.EpisodeId, sc.SeriesId, sc.Title, ss.SeasonNumber,
                   se.EpisodeNumber, se.Title, se.VideoId
            FROM SeriesEpisodes se
            JOIN SeriesSeasons ss ON ss.SeasonId=se.SeasonId
            JOIN SeriesCatalog sc ON sc.SeriesId=ss.SeriesId
            ORDER BY sc.Title COLLATE NOCASE, ss.SeasonNumber, se.EpisodeNumber
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<MissingEpisodeRecord>();
        while (r.Read())
        {
            var videoId = r.GetString(6);
            if (!available.Contains(videoId))
                rows.Add(new MissingEpisodeRecord(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetString(5), videoId));
        }
        return rows;
    }

    public object GetCatalog(LibraryService library, VideoMetadataService metadata, IReadOnlySet<string>? allowedSeriesIds = null, IReadOnlySet<string>? allowedVideoIds = null)
    {
        // Load each source into memory before composing the response. This avoids
        // opening nested SQLite readers on the same connection while one is active.
        var files = library.List().ToDictionary(x => x.Id);
        var metadataById = metadata.List().ToDictionary(x => x.VideoId);
        var seriesRows = ListSeries();
        if (allowedSeriesIds is not null)
            seriesRows = seriesRows.Where(x => allowedSeriesIds.Contains(x.SeriesId)).ToArray();

        var directVideoMap = new Dictionary<string, List<(string VideoId, int? EpisodeNumber, string Title)>>(StringComparer.OrdinalIgnoreCase);
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT SeriesId, VideoId, EpisodeNumber, Title FROM SeriesDirectVideos ORDER BY COALESCE(EpisodeNumber, 2147483647), Title COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var videoId = r.GetString(1);
                if (allowedVideoIds is not null && !allowedVideoIds.Contains(videoId)) continue;
                if (!directVideoMap.TryGetValue(r.GetString(0), out var rows)) directVideoMap[r.GetString(0)] = rows = new List<(string VideoId, int? EpisodeNumber, string Title)>();
                rows.Add((videoId, r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3)));
            }
        }

        var seasonMap = new Dictionary<long, (SeasonRecord Season, List<EpisodeRecord> Episodes)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ss.SeasonId, ss.SeriesId, ss.SeasonNumber, ss.Title, ss.FolderPath,
                       se.EpisodeId, se.VideoId, se.EpisodeNumber, se.Title, se.PosterPath
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
                    var season = new SeasonRecord(seasonId, r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
                    seasonMap.Add(seasonId, (season, new List<EpisodeRecord>()));
                }

                if (!r.IsDBNull(5))
                {
                    var videoId = r.GetString(6);
                    // Public viewers must only receive episodes whose source videos are published.
                    if (allowedVideoIds is null || allowedVideoIds.Contains(videoId))
                    {
                        seasonMap[seasonId].Episodes.Add(new EpisodeRecord(
                            r.GetInt64(5), seasonId, videoId, r.GetInt32(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
                    }
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
                        .Select(episode =>
                        {
                            var hasFile = files.TryGetValue(episode.VideoId, out var file);
                            metadataById.TryGetValue(episode.VideoId, out var videoMetadata);
                            var title = string.IsNullOrWhiteSpace(episode.Title)
                                ? (videoMetadata?.Title ?? file?.Title ?? "حلقة")
                                : episode.Title;
                            return new
                            {
                                episodeId = episode.EpisodeId,
                                videoId = episode.VideoId,
                                episodeNumber = episode.EpisodeNumber,
                                title,
                                posterPath = string.IsNullOrWhiteSpace(episode.PosterPath) ? series.PosterPath : episode.PosterPath,
                                url = hasFile ? file!.Url : null,
                                isMissing = !hasFile,
                                director = videoMetadata?.Director,
                                cast = videoMetadata?.Cast,
                                rating = videoMetadata?.Rating,
                                runtimeMinutes = videoMetadata?.RuntimeMinutes,
                                releaseDate = videoMetadata?.ReleaseDate,
                                tags = videoMetadata?.Tags
                            };
                        })
                        .ToArray()
                })
                .ToArray();

            var ratings = seasons.SelectMany(x => x.episodes)
                .Select(x => metadataById.TryGetValue(x.videoId, out var value) ? value?.Rating : null)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToArray();

            return new
            {
                seriesId = series.SeriesId,
                title = series.Title,
                organizationType = series.OrganizationType,
                directVideos = directVideoMap.TryGetValue(series.SeriesId, out var directIds) ? directIds.Select(entry =>
                {
                    var videoId = entry.VideoId;
                    var hasFile = files.TryGetValue(videoId, out var file);
                    metadataById.TryGetValue(videoId, out var videoMetadata);
                    return new { videoId, episodeNumber = entry.EpisodeNumber, title = string.IsNullOrWhiteSpace(entry.Title) ? (videoMetadata?.Title ?? file?.Title ?? "فيديو") : entry.Title, url = hasFile ? file!.Url : null, isMissing = !hasFile, director = videoMetadata?.Director, cast = videoMetadata?.Cast, rating = videoMetadata?.Rating, runtimeMinutes = videoMetadata?.RuntimeMinutes, releaseDate = videoMetadata?.ReleaseDate };
                }).ToArray() : Array.Empty<object>(),
                description = series.Description,
                year = series.Year,
                genre = series.Genre,
                posterPath = series.PosterPath,
                categoryId = series.CategoryId,
                section = series.Section,
                isPublished = series.IsPublished,
                seasonCount = seasons.Length,
                episodeCount = seasons.Sum(x => x.episodes.Length),
                rating = ratings.Length == 0 ? (double?)null : Math.Round(ratings.Average(), 1),
                seasons
            };
        }).ToArray();
    }
}
