using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record RestHouseMediaItem(
    string Id, string Kind, string Title, string FileName, string RelativePath,
    long SizeBytes, string ModifiedUtc, string Url);

public sealed record ScanMediaLibraryRequest(string Kind);

/// <summary>
/// Separate, non-video libraries for audio, books and applications.
/// Files remain in their original folders; the database stores catalog metadata only.
/// </summary>
public sealed class MultiMediaLibraryService
{
    private readonly string _root;
    private readonly string _connectionString;
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Extensions =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Audio"] = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac" },
            ["Books"] = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".epub", ".txt", ".cbz", ".cbr" },
            ["Apps"] = new(StringComparer.OrdinalIgnoreCase) { ".apk", ".xapk", ".apks" }
        };

    public MultiMediaLibraryService(string libraryRoot, string databasePath)
    {
        _root = Path.GetFullPath(libraryRoot);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, DefaultTimeout = 10, Pooling = true
        }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaLibraryItems(
                Id TEXT PRIMARY KEY,
                Kind TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Title TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE(Kind, RelativePath)
            );
            CREATE INDEX IF NOT EXISTS IX_MediaLibraryItems_Kind_Title
                ON MediaLibraryItems(Kind, Title COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
        foreach (var kind in Extensions.Keys) Directory.CreateDirectory(Path.Combine(_root, kind));
    }

    public IReadOnlyList<RestHouseMediaItem> List(string? kind = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, RelativePath, Title
            FROM MediaLibraryItems
            WHERE ($kind IS NULL OR Kind=$kind)
            ORDER BY Kind, Title COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<RestHouseMediaItem>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var itemKind = reader.GetString(1);
            var relative = reader.GetString(2);
            var title = reader.GetString(3);
            var path = Resolve(relative);
            if (path is null || !File.Exists(path)) continue;
            var info = new FileInfo(path);
            result.Add(new(id, itemKind, title, info.Name, relative, info.Length,
                info.LastWriteTimeUtc.ToString("O"), "/api/media-library/" + Uri.EscapeDataString(id)));
        }
        return result;
    }

    public int Scan(string kind)
    {
        if (!Extensions.TryGetValue(kind, out var allowed))
            throw new ArgumentException("نوع المكتبة غير مدعوم.");
        var folder = Path.Combine(_root, kind);
        Directory.CreateDirectory(folder);
        var count = 0;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!allowed.Contains(Path.GetExtension(file))) continue;
            var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + relative)))[..32].ToLowerInvariant();
            var title = Path.GetFileNameWithoutExtension(file).Trim();
            if (title.Length == 0) title = Path.GetFileName(file);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO MediaLibraryItems(Id,Kind,RelativePath,Title,UpdatedUtc)
                VALUES($id,$kind,$path,$title,$updated)
                ON CONFLICT(Kind,RelativePath) DO UPDATE SET
                    Title=excluded.Title, UpdatedUtc=excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$path", relative);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            count++;
        }
        transaction.Commit();
        return count;
    }

    public string? ResolveById(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath FROM MediaLibraryItems WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        var relative = command.ExecuteScalar() as string;
        var path = relative is null ? null : Resolve(relative);
        return path is not null && File.Exists(path) ? path : null;
    }

    private string? Resolve(string relative)
    {
        try
        {
            var root = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }
}
