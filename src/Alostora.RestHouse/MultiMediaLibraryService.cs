using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record RestHouseMediaItem(
    string Id, string Kind, string Title, string FileName, string RelativePath,
    long SizeBytes, string ModifiedUtc, string Url);

public sealed record ScanMediaLibraryRequest(string Kind);
public sealed record AddMediaLibraryRootRequest(string Kind, string FolderPath);
public sealed record MediaLibraryRoot(string Id, string Kind, string Path, bool IsDefault);
public sealed record MediaStorageRootSummary(
    string Id, string Kind, string Path, bool IsDefault, int IndexedFiles,
    long IndexedBytes, long? DriveTotalBytes, long? DriveFreeBytes);
public sealed record MediaStorageSummary(
    int RootCount, int IndexedFiles, long IndexedBytes,
    long? DriveTotalBytes, long? DriveFreeBytes,
    IReadOnlyList<MediaStorageRootSummary> Roots);

/// <summary>
/// Separate audio, book and application libraries. Multiple source folders per kind are supported.
/// Source files are never copied, moved or deleted by this service.
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
            CREATE TABLE IF NOT EXISTS MediaLibraryRoots(
                Id TEXT PRIMARY KEY,
                Kind TEXT NOT NULL,
                RootPath TEXT NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                UNIQUE(Kind, RootPath)
            );
            """;
        command.ExecuteNonQuery();

        foreach (var kind in Extensions.Keys)
        {
            var defaultPath = Path.GetFullPath(Path.Combine(_root, kind));
            Directory.CreateDirectory(defaultPath);
            RegisterDefaultRoot(kind, defaultPath);
        }
    }

    public IReadOnlyList<MediaLibraryRoot> ListRoots(string? kind = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, RootPath, IsDefault FROM MediaLibraryRoots
            WHERE ($kind IS NULL OR Kind=$kind)
            ORDER BY Kind, IsDefault DESC, RootPath COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<MediaLibraryRoot>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        return result;
    }

    public MediaLibraryRoot AddRoot(string kind, string folderPath)
    {
        EnsureKind(kind);
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("حدد مسار المجلد أولاً.");
        var fullPath = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("المجلد المحدد غير موجود أو لا يمكن الوصول إليه.");
        var roots = ListRoots(kind);
        if (roots.Any(x => IsWithin(fullPath, x.Path) || IsWithin(x.Path, fullPath)))
            throw new ArgumentException("المجلد المحدد يطابق مجلداً مسجلاً أو يتداخل معه. اختر مجلداً مستقلاً لتجنب تكرار الملفات.");
        var id = RootId(kind, fullPath);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MediaLibraryRoots(Id,Kind,RootPath,IsDefault,CreatedUtc)
            VALUES($id,$kind,$path,0,$created);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return new(id, kind, fullPath, false);
    }

    public bool RemoveRoot(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MediaLibraryRoots WHERE Id=$id AND IsDefault=0";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
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
        var roots = ListRoots();
        var result = new List<RestHouseMediaItem>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var itemKind = reader.GetString(1);
            var relative = reader.GetString(2);
            var title = reader.GetString(3);
            var path = Resolve(relative, roots);
            if (path is null || !File.Exists(path)) continue;
            var info = new FileInfo(path);
            result.Add(new(id, itemKind, title, info.Name, relative, info.Length,
                info.LastWriteTimeUtc.ToString("O"), "/api/media-library/" + Uri.EscapeDataString(id)));
        }
        return result;
    }

    public int Scan(string kind)
    {
        EnsureKind(kind);
        var roots = ListRoots(kind);
        var count = 0;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            foreach (var file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
            {
                if (!Extensions[kind].Contains(Path.GetExtension(file))) continue;
                var relativeWithinRoot = Path.GetRelativePath(root.Path, file).Replace('\\', '/');
                var key = root.IsDefault
                    ? Path.GetRelativePath(_root, file).Replace('\\', '/')
                    : "@" + root.Id + "/" + relativeWithinRoot;
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + key)))[..32].ToLowerInvariant();
                var title = Path.GetFileNameWithoutExtension(file).Trim();
                if (title.Length == 0) title = Path.GetFileName(file);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO MediaLibraryItems(Id,Kind,RelativePath,Title,UpdatedUtc)
                    VALUES($id,$kind,$path,$title,$updated)
                    ON CONFLICT(Kind,RelativePath) DO UPDATE SET
                        Id=excluded.Id, Title=excluded.Title, UpdatedUtc=excluded.UpdatedUtc;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$path", key);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                count++;
            }
        }
        transaction.Commit();
        return count;
    }

    public MediaStorageSummary GetStorageSummary()
    {
        var items = List();
        var roots = ListRoots();
        var summaries = new List<MediaStorageRootSummary>();
        foreach (var root in roots)
        {
            var rootItems = items.Where(item =>
            {
                var path = Resolve(item.RelativePath, roots);
                return item.Kind == root.Kind && path is not null && IsWithin(path, root.Path);
            }).ToArray();
            long? total = null, free = null;
            try
            {
                var driveRoot = Path.GetPathRoot(root.Path);
                if (!string.IsNullOrWhiteSpace(driveRoot))
                {
                    var drive = new DriveInfo(driveRoot);
                    if (drive.IsReady) { total = drive.TotalSize; free = drive.AvailableFreeSpace; }
                }
            }
            catch { }
            summaries.Add(new(root.Id, root.Kind, root.Path, root.IsDefault,
                rootItems.Length, rootItems.Sum(x => x.SizeBytes), total, free));
        }
        var drives = roots.Select(x => Path.GetPathRoot(x.Path))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => { try { var d = new DriveInfo(x!); return d.IsReady ? (Total: (long?)d.TotalSize, Free: (long?)d.AvailableFreeSpace) : (null, null); } catch { return ((long?)null, (long?)null); } })
            .ToArray();
        return new(roots.Count, items.Count, items.Sum(x => x.SizeBytes),
            drives.Any(x => x.Total.HasValue) ? drives.Sum(x => x.Total ?? 0) : null,
            drives.Any(x => x.Free.HasValue) ? drives.Sum(x => x.Free ?? 0) : null,
            summaries);
    }

    public string? ResolveById(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath FROM MediaLibraryItems WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        var relative = command.ExecuteScalar() as string;
        var path = relative is null ? null : Resolve(relative, ListRoots());
        return path is not null && File.Exists(path) ? path : null;
    }

    private void RegisterDefaultRoot(string kind, string path)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO MediaLibraryRoots(Id,Kind,RootPath,IsDefault,CreatedUtc)
            VALUES($id,$kind,$path,1,$created);
            """;
        command.Parameters.AddWithValue("$id", RootId(kind, path));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private string? Resolve(string relative, IReadOnlyList<MediaLibraryRoot>? roots = null)
    {
        try
        {
            if (relative.StartsWith('@'))
            {
                var separator = relative.IndexOf('/');
                if (separator <= 1) return null;
                var rootId = relative[1..separator];
                roots ??= ListRoots();
                var root = roots.FirstOrDefault(x => x.Id == rootId);
                if (root is null) return null;
                var full = Path.GetFullPath(Path.Combine(root.Path, relative[(separator + 1)..].Replace('/', Path.DirectorySeparatorChar)));
                return IsWithin(full, root.Path) ? full : null;
            }
            var managed = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return IsWithin(managed, _root) ? managed : null;
        }
        catch { return null; }
    }

    private static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   basePath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string RootId(string kind, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + Path.GetFullPath(path).ToUpperInvariant())))[..24].ToLowerInvariant();

    private static void EnsureKind(string kind)
    {
        if (!Extensions.ContainsKey(kind)) throw new ArgumentException("نوع المكتبة غير مدعوم.");
    }
}
