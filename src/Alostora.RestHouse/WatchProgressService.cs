using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record WatchProgressRequest(string ViewerId, string VideoId, double PositionSeconds, double DurationSeconds);
public sealed record WatchProgressItem(string VideoId, double PositionSeconds, double DurationSeconds, DateTime UpdatedUtc);

public sealed class WatchProgressService
{
    private readonly string _connectionString;

    public WatchProgressService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS WatchProgress (
                ViewerId TEXT NOT NULL,
                VideoId TEXT NOT NULL,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (ViewerId, VideoId)
            );
            CREATE INDEX IF NOT EXISTS IX_WatchProgress_UpdatedUtc ON WatchProgress(UpdatedUtc);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WatchProgressItem> Get(string viewerId)
    {
        if (string.IsNullOrWhiteSpace(viewerId)) return [];
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VideoId, PositionSeconds, DurationSeconds, UpdatedUtc FROM WatchProgress WHERE ViewerId = $viewer ORDER BY UpdatedUtc DESC";
        command.Parameters.AddWithValue("$viewer", viewerId.Trim());
        using var reader = command.ExecuteReader();
        var items = new List<WatchProgressItem>();
        while (reader.Read())
            items.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return items;
    }

    public void Save(WatchProgressRequest request)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WatchProgress(ViewerId, VideoId, PositionSeconds, DurationSeconds, UpdatedUtc)
            VALUES($viewer, $video, $position, $duration, $updated)
            ON CONFLICT(ViewerId, VideoId) DO UPDATE SET
                PositionSeconds = excluded.PositionSeconds,
                DurationSeconds = excluded.DurationSeconds,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$viewer", request.ViewerId.Trim());
        command.Parameters.AddWithValue("$video", request.VideoId.Trim());
        command.Parameters.AddWithValue("$position", Math.Max(0, request.PositionSeconds));
        command.Parameters.AddWithValue("$duration", Math.Max(0, request.DurationSeconds));
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
