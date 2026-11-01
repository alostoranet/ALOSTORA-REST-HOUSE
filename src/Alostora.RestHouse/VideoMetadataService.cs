using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record VideoMetadata(string VideoId, string Title, string Description, int? Year, string Genre, string Kind, string? PosterPath);
public sealed record SaveVideoMetadataRequest(string VideoId, string Title, string? Description, int? Year, string? Genre, string? Kind, string? PosterPath);

public sealed class VideoMetadataService
{
    private readonly string _connectionString;

    public VideoMetadataService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS VideoMetadata(
              VideoId TEXT PRIMARY KEY,
              Title TEXT NOT NULL,
              Description TEXT NOT NULL DEFAULT '',
              Year INTEGER NULL,
              Genre TEXT NOT NULL DEFAULT '',
              Kind TEXT NOT NULL DEFAULT 'Movie',
              PosterPath TEXT NULL,
              UpdatedUtc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public VideoMetadata? Get(string id)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT VideoId,Title,Description,Year,Genre,Kind,PosterPath FROM VideoMetadata WHERE VideoId=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new VideoMetadata(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6))
            : null;
    }

    public IReadOnlyList<VideoMetadata> List()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT VideoId,Title,Description,Year,Genre,Kind,PosterPath FROM VideoMetadata ORDER BY Title COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var rows = new List<VideoMetadata>();
        while (r.Read())
            rows.Add(new VideoMetadata(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return rows;
    }

    public void Save(SaveVideoMetadataRequest x)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VideoMetadata(VideoId,Title,Description,Year,Genre,Kind,PosterPath,UpdatedUtc)
            VALUES($id,$title,$description,$year,$genre,$kind,$poster,$updated)
            ON CONFLICT(VideoId) DO UPDATE SET
              Title=excluded.Title,
              Description=excluded.Description,
              Year=excluded.Year,
              Genre=excluded.Genre,
              Kind=excluded.Kind,
              PosterPath=excluded.PosterPath,
              UpdatedUtc=excluded.UpdatedUtc;
            """;
        cmd.Parameters.AddWithValue("$id", x.VideoId);
        cmd.Parameters.AddWithValue("$title", x.Title.Trim());
        cmd.Parameters.AddWithValue("$description", x.Description ?? "");
        cmd.Parameters.AddWithValue("$year", (object?)x.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genre", x.Genre ?? "");
        cmd.Parameters.AddWithValue("$kind", x.Kind == "Series" ? "Series" : "Movie");
        var existing = Get(x.VideoId);
        var posterPath = x.PosterPath ?? existing?.PosterPath;
        cmd.Parameters.AddWithValue("$poster", (object?)posterPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
